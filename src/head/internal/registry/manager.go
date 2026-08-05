package registry

import (
	"archive/tar"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"

	"github.com/google/go-containerregistry/pkg/crane"
	"github.com/google/go-containerregistry/pkg/name"
	v1 "github.com/google/go-containerregistry/pkg/v1"
)

// Manager handles OCI registry operations for binary management
type Manager struct {
	logger       *slog.Logger
	cache        string
	pulledDigest map[string]string // source → resolved image digest (sha256:...)
	mu           sync.RWMutex
}

func NewManager(logger *slog.Logger, cacheDir string) *Manager {
	return &Manager{
		logger:       logger,
		cache:        cacheDir,
		pulledDigest: make(map[string]string),
	}
}

// PulledBinaryInfo returns the resolved image digest for a previously
// pulled source. Returns empty string if the source was never pulled
// in this process lifetime.
//
// Prefer ResolveDigest for /status provenance: this map only covers pulls
// made by *this* process, so it is empty after any restart that skipped the
// pull.
func (m *Manager) PulledBinaryInfo(source string) string {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.pulledDigest[source]
}

// DigestSidecarPath is where the resolved image digest is recorded next to a
// pulled binary, so provenance survives a restart that skips the pull.
func DigestSidecarPath(destination string) string {
	return destination + ".digest"
}

// RecordDigest persists the resolved image digest beside the binary. A failure
// here is not fatal — it costs provenance on /status, not correctness — so the
// caller logs and continues.
func RecordDigest(destination, digest string) error {
	return os.WriteFile(DigestSidecarPath(destination), []byte(digest), 0o644)
}

// ResolveDigest returns the image digest for a binary, preferring the digest
// recorded by a pull in this process and falling back to the on-disk sidecar.
//
// Without the fallback, /status reports an empty pulled_digest whenever the
// pull was skipped (checksum match, or a restart with the binary already in
// place), which makes deploy-time digest verification fail for a healthy node.
func (m *Manager) ResolveDigest(source, destination string) string {
	if d := m.PulledBinaryInfo(source); d != "" {
		return d
	}
	if destination == "" {
		return ""
	}
	data, err := os.ReadFile(DigestSidecarPath(destination))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(data))
}

// fileEntry is one regular-file entry inside a layer tar.
type fileEntry struct {
	rawName    string // original header.Name
	normalized string // "./" and "/" stripped
	dir        string // directory part of normalized
	base       string // basename
	size       int64
}

// PullBinary pulls a binary from an OCI registry. See extractBinary for
// the multi-file (shared-library) handling.
//
// destination must end in /<binaryName>: the launcher lands at
// destination, any sibling files (e.g. lib*.so*) land at the same
// directory. filepath.Base(destination) must equal binaryName —
// enforced below.
func (m *Manager) PullBinary(source, destination, imageDigest, binaryChecksum, binaryName string) error {
	if filepath.Base(destination) != binaryName {
		return fmt.Errorf("destination %q does not end in /%s (binaryName); refusing to extract to a path that conflicts with single-file vs multi-file layouts", destination, binaryName)
	}

	m.logger.Info("pulling binary from registry",
		"source", source, "destination", destination,
		"image_digest", imageDigest, "binary_checksum", binaryChecksum,
		"binary_name", binaryName)

	ref, err := name.ParseReference(source)
	if err != nil {
		return fmt.Errorf("parse image reference: %w", err)
	}
	img, err := crane.Pull(ref.String())
	if err != nil {
		return fmt.Errorf("pull image: %w", err)
	}
	digest, err := img.Digest()
	if err != nil {
		return fmt.Errorf("get image digest: %w", err)
	}
	m.logger.Info("image pulled", "digest", digest.String())

	if imageDigest != "" {
		if err := m.verifyImageDigest(digest, imageDigest); err != nil {
			return err
		}
	}

	if err := m.extractBinary(img, destination, binaryName); err != nil {
		return fmt.Errorf("extract binary: %w", err)
	}

	// chmod 0755 is a no-op for multi-file (already set during write),
	// and required for the single-file path (writeFileAtomic used 0644).
	if err := os.Chmod(destination, 0755); err != nil {
		return fmt.Errorf("chmod binary: %w", err)
	}

	if binaryChecksum != "" {
		actualChecksum, err := ComputeChecksum(destination)
		if err != nil {
			return fmt.Errorf("compute binary checksum: %w", err)
		}
		if actualChecksum != binaryChecksum {
			os.Remove(destination)
			return fmt.Errorf("binary checksum mismatch: got %s, expected %s", actualChecksum, binaryChecksum)
		}
		m.logger.Info("binary checksum verified", "checksum", actualChecksum)
	}

	// Track the resolved digest for /status provenance, both in-process and
	// on disk so it survives a restart that skips the pull.
	m.mu.Lock()
	m.pulledDigest[source] = digest.String()
	m.mu.Unlock()
	if err := RecordDigest(destination, digest.String()); err != nil {
		m.logger.Warn("could not record digest sidecar; /status provenance will be unavailable after restart",
			"destination", destination, "error", err)
	}

	m.logger.Info("binary pulled successfully",
		"source", source, "destination", destination, "digest", digest.String())
	return nil
}

func (m *Manager) verifyImageDigest(actual v1.Hash, expected string) error {
	parts := strings.SplitN(expected, ":", 2)
	if len(parts) != 2 {
		return fmt.Errorf("invalid digest format: %s (expected sha256:hex)", expected)
	}
	if parts[0] != "sha256" {
		return fmt.Errorf("unsupported digest algorithm: %s (only sha256 supported)", parts[0])
	}
	if actual.Hex != parts[1] {
		return fmt.Errorf("image digest mismatch: got %s, expected %s", actual.Hex, parts[1])
	}
	m.logger.Info("image digest verified", "digest", actual.String())
	return nil
}

// extractBinary walks the image layers and routes to the appropriate
// extraction path. See extractFromTar for the detection rules.
//
// Layers are visited in REVERSE order (top layer first). The image is
// built as a small COPY layer (the binary + libs, ~188 MB) stacked on a
// large CUDA base (~4 GB); the binary lives in the COPY layer, so starting
// from the top finds it on the first layer instead of decompressing every
// base layer just to confirm the binary is not there.
func (m *Manager) extractBinary(img v1.Image, destination, binaryName string) error {
	layers, err := img.Layers()
	if err != nil {
		return fmt.Errorf("get layers: %w", err)
	}
	destDir := filepath.Dir(destination)
	if err := os.MkdirAll(destDir, 0755); err != nil {
		return fmt.Errorf("create destination directory: %w", err)
	}

	for i := len(layers) - 1; i >= 0; i-- {
		layer := layers[i]
		m.logger.Debug("processing layer", "index", i, "from_top", len(layers)-1-i)
		rc, err := layer.Uncompressed()
		if err != nil {
			return fmt.Errorf("uncompress layer %d: %w", i, err)
		}
		layout, err := m.extractFromTar(rc, destination, binaryName)
		rc.Close()
		if err != nil {
			return fmt.Errorf("extract from layer %d: %w", i, err)
		}
		if layout != "" {
			m.logger.Debug("binary found in layer", "index", i, "layout", layout)
			return nil
		}
	}
	return fmt.Errorf("binary '%s' not found in any layer", binaryName)
}

// extractFromTar walks the layer tar ONCE, streams every regular file
// body to a temp file in destDir, then promotes the launcher (and its
// siblings, for a shared-library layout) into place. Returns "" if the
// binary is not present in this layer.
//
// Bounded memory: bodies are streamed via io.Copy, never buffered whole
// in RAM (the previous implementation held every file body and, for the
// large CUDA base layers, the whole uncompressed layer — the P100 image
// peaked at ~11.7 GB RSS).
//
// Multi-file detection: if the layer contains the named binary AND
// any other regular file in the same directory, the layer is treated
// as a shared-library build. All files in that directory are
// extracted together to destDir/<basename>. Otherwise the legacy
// single-file path is used (only the launcher is extracted to
// destination).
//
// Crash-safety: every file is written via temp + fsync + rename (the
// old writeFileAtomic semantics, inlined for streaming). The destDir is
// NEVER removed or renamed, so a crash mid-extract leaves the old
// contents intact (the partial new files are in destDir/.partial-*.tmp
// and are cleaned up on the next pull).
func (m *Manager) extractFromTar(r io.Reader, destination, binaryName string) (string, error) {
	// Single pass over the tar: stream each file body to a temp file in
	// destDir and remember its header, so we can promote (rename) the
	// launcher + siblings after we know the layout.
	type fileWithTmp struct {
		entry   fileEntry
		tmpPath string
	}
	destDir := filepath.Dir(destination)
	if err := os.MkdirAll(destDir, 0755); err != nil {
		return "", fmt.Errorf("create destination directory: %w", err)
	}
	dstLocks.Lock(destDir)
	defer dstLocks.Unlock(destDir)

	var files []fileWithTmp
	tr := tar.NewReader(r)
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return "", fmt.Errorf("read tar: %w", err)
		}
		if hdr.Typeflag != tar.TypeReg {
			continue
		}
		raw := hdr.Name
		norm := strings.TrimPrefix(raw, "./")
		norm = strings.TrimPrefix(norm, "/")
		dir, base := filepath.Split(norm)
		dir = strings.TrimSuffix(dir, "/")

		tmp, err := os.CreateTemp(destDir, ".partial-*.tmp")
		if err != nil {
			return "", fmt.Errorf("create temp for %q: %w", raw, err)
		}
		tmpPath := tmp.Name()
		if _, err := io.Copy(tmp, tr); err != nil {
			tmp.Close()
			os.Remove(tmpPath)
			return "", fmt.Errorf("write body for %q: %w", raw, err)
		}
		if err := tmp.Sync(); err != nil {
			tmp.Close()
			os.Remove(tmpPath)
			return "", fmt.Errorf("fsync temp for %q: %w", raw, err)
		}
		if err := tmp.Close(); err != nil {
			os.Remove(tmpPath)
			return "", fmt.Errorf("close temp for %q: %w", raw, err)
		}
		files = append(files, fileWithTmp{
			entry:   fileEntry{rawName: raw, normalized: norm, dir: dir, base: base, size: hdr.Size},
			tmpPath: tmpPath,
		})
	}

	// Find the launcher
	var launcher *fileWithTmp
	for i := range files {
		if files[i].entry.base == binaryName {
			launcher = &files[i]
			break
		}
	}
	if launcher == nil {
		// Binary not in this layer: discard the temps, report nothing.
		for _, f := range files {
			os.Remove(f.tmpPath)
		}
		return "", nil
	}

	launcherDir := launcher.entry.dir
	var siblings []fileWithTmp
	for i := range files {
		if files[i].entry.dir == launcherDir {
			siblings = append(siblings, files[i])
		}
	}

	if len(siblings) <= 1 {
		// Single-file path: promote the launcher temp into destination.
		// Mode 0755 (executable).
		if err := os.Rename(launcher.tmpPath, destination); err != nil {
			os.Remove(launcher.tmpPath)
			return "", fmt.Errorf("write launcher: %w", err)
		}
		if err := os.Chmod(destination, 0755); err != nil {
			return "", fmt.Errorf("chmod launcher: %w", err)
		}
		for _, f := range files {
			if f.tmpPath != launcher.tmpPath {
				os.Remove(f.tmpPath)
			}
		}
		m.logger.Debug("extracted single-file binary",
			"path", destination, "bytes", launcher.entry.size,
			"source", launcher.entry.rawName)
		return "single-file", nil
	}

	// Multi-file path: promote each sibling temp into destDir. The
	// destDir is NOT removed or renamed — we just write into it. If
	// the old contents had files that aren't in the new layer (e.g.
	// an old version of libfoo.so), they linger harmlessly; the new
	// files are the ones that matter. Per-file temp + rename makes
	// this crash-safe: a crash mid-extract leaves a .partial-*.tmp
	// file in destDir that gets cleaned up on the next pull.
	for _, sib := range siblings {
		mode := int64(0644)
		if sib.entry.base == binaryName {
			mode = 0755
		}
		out := filepath.Join(destDir, sib.entry.base)
		if err := os.Rename(sib.tmpPath, out); err != nil {
			return "", fmt.Errorf("write %s: %w", sib.entry.base, err)
		}
		if err := os.Chmod(out, os.FileMode(mode)); err != nil {
			return "", fmt.Errorf("chmod %s: %w", sib.entry.base, err)
		}
		m.logger.Debug("extracted file",
			"path", out, "bytes", sib.entry.size, "source", sib.entry.rawName,
			"mode", fmt.Sprintf("%o", mode))
	}
	for _, f := range files {
		if f.entry.dir != launcherDir {
			os.Remove(f.tmpPath)
		}
	}
	return "multi:" + launcherDir, nil
}

// writeFileAtomic writes body to path via temp-file + fsync + rename.
// Mode is applied to both the temp file (in case the rename fails
// and the temp file is left behind) and the final path. The temp
// file lives in the same dir as path so the rename is on the same
// filesystem (atomic on POSIX).
func writeFileAtomic(path string, body []byte, mode int64) error {
	dir := filepath.Dir(path)
	tmp, err := os.CreateTemp(dir, ".partial-*.tmp")
	if err != nil {
		return fmt.Errorf("create temp: %w", err)
	}
	tmpPath := tmp.Name()
	if err := tmp.Chmod(os.FileMode(mode)); err != nil {
		tmp.Close()
		os.Remove(tmpPath)
		return fmt.Errorf("chmod temp: %w", err)
	}
	if _, err := tmp.Write(body); err != nil {
		tmp.Close()
		os.Remove(tmpPath)
		return fmt.Errorf("write: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		os.Remove(tmpPath)
		return fmt.Errorf("fsync: %w", err)
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpPath)
		return fmt.Errorf("close: %w", err)
	}
	if err := os.Rename(tmpPath, path); err != nil {
		os.Remove(tmpPath)
		return fmt.Errorf("rename: %w", err)
	}
	// Belt-and-braces: ensure the final path has the right mode
	// (some filesystems reset the mode across the rename).
	if err := os.Chmod(path, os.FileMode(mode)); err != nil {
		return fmt.Errorf("chmod final: %w", err)
	}
	return nil
}

// ComputeChecksum computes the sha256 checksum of a file
func ComputeChecksum(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", fmt.Errorf("open file: %w", err)
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", fmt.Errorf("compute hash: %w", err)
	}
	return "sha256:" + hex.EncodeToString(h.Sum(nil)), nil
}

// PullAndVerify pulls a binary and verifies the checksum
func (m *Manager) PullAndVerify(source, destination, imageDigest, binaryChecksum, binaryName string) (string, error) {
	if err := m.PullBinary(source, destination, imageDigest, binaryChecksum, binaryName); err != nil {
		return "", err
	}
	actualChecksum, err := ComputeChecksum(destination)
	if err != nil {
		return "", fmt.Errorf("compute checksum: %w", err)
	}
	return actualChecksum, nil
}

// dstLocks serialises concurrent extractions to the same dest dir.
// Per-destination sync.Mutex is held briefly during the write loop.
// Map is bounded by the number of distinct dest dirs in the system
// (typically a handful).
var dstLocks = newKeyMutex()

type keyMutex struct {
	mu sync.Mutex
	m  map[string]*sync.Mutex
}

func newKeyMutex() *keyMutex { return &keyMutex{m: map[string]*sync.Mutex{}} }

func (k *keyMutex) Lock(key string) {
	k.mu.Lock()
	km, ok := k.m[key]
	if !ok {
		km = &sync.Mutex{}
		k.m[key] = km
	}
	k.mu.Unlock()
	km.Lock()
}

func (k *keyMutex) Unlock(key string) {
	k.mu.Lock()
	km := k.m[key]
	k.mu.Unlock()
	if km != nil {
		km.Unlock()
	}
}
