package registry

import (
	"archive/tar"
	"bytes"
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
	logger *slog.Logger
	cache  string
}

func NewManager(logger *slog.Logger, cacheDir string) *Manager {
	return &Manager{logger: logger, cache: cacheDir}
}

// fileEntry is one regular-file entry inside a layer tar.
type fileEntry struct {
	rawName    string // original header.Name (e.g. "llama/llama-server")
	normalized string // "./" and "/" stripped
	dir        string // directory part of normalized (e.g. "llama") — "" if at root
	base       string // basename (e.g. "llama-server")
	size       int64
}

// PullBinary pulls a binary from an OCI registry. See extractBinary for
// the multi-file (shared-library) handling.
func (m *Manager) PullBinary(source, destination, imageDigest, binaryChecksum, binaryName string) error {
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

// extractBinary walks the image layers, finds the layer containing the
// named binary, and routes to either the single-file or multi-file
// extraction path. See extractFromTar for the detection rules.
func (m *Manager) extractBinary(img v1.Image, destination, binaryName string) error {
	layers, err := img.Layers()
	if err != nil {
		return fmt.Errorf("get layers: %w", err)
	}
	destDir := filepath.Dir(destination)
	if err := os.MkdirAll(destDir, 0755); err != nil {
		return fmt.Errorf("create destination directory: %w", err)
	}

	for i, layer := range layers {
		m.logger.Debug("processing layer", "index", i)
		rc, err := layer.Uncompressed()
		if err != nil {
			return fmt.Errorf("uncompress layer %d: %w", i, err)
		}
		// Buffer the whole layer. Shared-lib layers uncompress to
		// ~100 MB max; for single-file images this is overkill but
		// lets one code path do both layouts.
		buf, err := io.ReadAll(rc)
		rc.Close()
		if err != nil {
			return fmt.Errorf("read layer %d: %w", i, err)
		}

		// bytes.NewReader is a ReadSeeker, which extractFromTar
		// needs to re-walk for each entry.
		layout, err := m.extractFromTar(bytes.NewReader(buf), destination, binaryName)
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

// extractFromTar walks the layer tar to classify the file layout, then
// extracts via the appropriate path. Returns "" if the binary is not
// present in this layer.
//
// Multi-file detection: if the layer contains the named binary AND
// any other regular file in the same directory, the layer is treated
// as a shared-library build. All files in that directory are
// extracted together via an atomic directory swap. Otherwise the
// legacy single-file path is used (atomic single-file rename,
// unchanged).
//
// The reader must be an io.ReadSeeker (e.g. *bytes.Reader wrapping a
// buffered layer) so re-walks can find each entry's body.
func (m *Manager) extractFromTar(r io.ReadSeeker, destination, binaryName string) (string, error) {
	var files []fileEntry
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
		files = append(files, fileEntry{
			rawName: raw, normalized: norm, dir: dir, base: base, size: hdr.Size,
		})
	}

	var launcher *fileEntry
	for i := range files {
		if files[i].base == binaryName {
			launcher = &files[i]
			break
		}
	}
	if launcher == nil {
		return "", nil
	}

	launcherDir := launcher.dir
	var siblings []fileEntry
	for i := range files {
		if files[i].dir == launcherDir {
			siblings = append(siblings, files[i])
		}
	}

	if len(siblings) <= 1 {
		// Single-file path: copy launcher bytes to a temp file,
		// then atomically rename. The layer is re-walked to find
		// the entry body (archive/tar is forward-only).
		launcherBytes, err := readTarFile(r, launcher.normalized)
		if err != nil {
			return "", fmt.Errorf("read launcher from tar: %w", err)
		}
		if err := writeFileAtomic(destination, launcherBytes); err != nil {
			return "", fmt.Errorf("write launcher: %w", err)
		}
		m.logger.Debug("extracted single-file binary",
			"path", destination, "bytes", len(launcherBytes),
			"source", launcher.rawName)
		return "single-file", nil
	}

	// Multi-file path: stage launcher + siblings into a temp dir,
	// then atomically rename the temp dir over the dest dir.
	// The dest dir will contain:
	//   <destDir>/<binaryName>          (the launcher)
	//   <destDir>/<sibling1>            (e.g. libllama-server-impl.so)
	//   <destDir>/<sibling2>            (e.g. libllama.so.0)
	//   ...
	destDir := filepath.Dir(destination)
	dstLocks.Lock(destDir)
	defer dstLocks.Unlock(destDir)

	// Stage into a sibling temp dir, then rename over the dest dir.
	// The temp dir lives next to destDir so the rename is on the same
	// filesystem (atomic on POSIX).
	parent := filepath.Dir(destDir)
	tempDir, err := os.MkdirTemp(parent, ".binary-multi-*.tmp")
	if err != nil {
		return "", fmt.Errorf("create temp dir: %w", err)
	}
	success := false
	defer func() {
		if !success {
			os.RemoveAll(tempDir)
		}
	}()

	for _, sib := range siblings {
		body, err := readTarFile(r, sib.normalized)
		if err != nil {
			return "", fmt.Errorf("read %s from tar: %w", sib.base, err)
		}
		out := filepath.Join(tempDir, sib.base)
		if err := writeFileAtomic(out, body); err != nil {
			return "", fmt.Errorf("write %s: %w", sib.base, err)
		}
		// Make the launcher executable in the staging dir. The
		// file mode survives the rename into destDir.
		if sib.isLauncherRaw(binaryName) {
			if err := os.Chmod(out, 0755); err != nil {
				return "", fmt.Errorf("chmod launcher: %w", err)
			}
		}
		m.logger.Debug("extracted file",
			"path", out, "bytes", len(body), "source", sib.rawName)
	}

	// Atomic directory swap. RemoveAll first because the dest dir
	// may be a mount point we can't replace in place.
	if err := os.RemoveAll(destDir); err != nil {
		return "", fmt.Errorf("remove old dest dir: %w", err)
	}
	if err := os.Rename(tempDir, destDir); err != nil {
		return "", fmt.Errorf("rename temp dir over dest: %w", err)
	}
	success = true
	return "multi:" + launcherDir, nil
}

// isLauncherRaw is a small helper to keep the multi-file loop readable.
func (e fileEntry) isLauncherRaw(binaryName string) bool { return e.base == binaryName }

// readTarFile re-walks the tar stream to find an entry by its normalized
// name and return its body bytes. The reader must be a *bytes.Reader
// (or any io.ReadSeeker) so the caller can reset it for subsequent
// re-walks. archive/tar is forward-only, so we re-seek to 0 before
// each call.
func readTarFile(r io.ReadSeeker, normalizedName string) ([]byte, error) {
	if _, err := r.Seek(0, io.SeekStart); err != nil {
		return nil, fmt.Errorf("seek: %w", err)
	}
	tr := tar.NewReader(r)
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			return nil, fmt.Errorf("entry %q not found in tar", normalizedName)
		}
		if err != nil {
			return nil, err
		}
		if hdr.Typeflag != tar.TypeReg {
			continue
		}
		raw := hdr.Name
		norm := strings.TrimPrefix(raw, "./")
		norm = strings.TrimPrefix(norm, "/")
		if norm != normalizedName {
			// Drain the body so the next Next() is at the right place
			// (only matters if we re-walk past this entry; safe
			// regardless).
			_, _ = io.Copy(io.Discard, tr)
			continue
		}
		return io.ReadAll(tr)
	}
}

// writeFileAtomic writes body to path via temp-file + fsync + rename.
func writeFileAtomic(path string, body []byte) error {
	dir := filepath.Dir(path)
	tmp, err := os.CreateTemp(dir, ".partial-*.tmp")
	if err != nil {
		return fmt.Errorf("create temp: %w", err)
	}
	tmpPath := tmp.Name()
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
// Per-destination sync.Mutex is held briefly during the atomic swap.
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
