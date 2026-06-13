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

	"github.com/google/go-containerregistry/pkg/crane"
	"github.com/google/go-containerregistry/pkg/name"
	v1 "github.com/google/go-containerregistry/pkg/v1"
)

// Manager handles OCI registry operations for binary management
type Manager struct {
	logger *slog.Logger
	cache  string // local cache directory
}

// NewManager creates a new registry manager
func NewManager(logger *slog.Logger, cacheDir string) *Manager {
	return &Manager{
		logger: logger,
		cache:  cacheDir,
	}
}

// PullBinary pulls a binary from an OCI registry
// source format: registry/image:tag or registry/image@sha256:digest
// destination: local path where binary should be placed
// imageDigest: optional OCI image manifest digest to verify (format: "sha256:abc123...")
// binaryChecksum: optional SHA256 checksum of the extracted binary file (format: "sha256:abc123...")
// binaryName: name of the binary to extract from the image (e.g., "llama-server", "busybox")
func (m *Manager) PullBinary(source, destination, imageDigest, binaryChecksum, binaryName string) error {
	m.logger.Info("pulling binary from registry",
		"source", source,
		"destination", destination,
		"image_digest", imageDigest,
		"binary_checksum", binaryChecksum,
		"binary_name", binaryName)

	// Parse the image reference
	ref, err := name.ParseReference(source)
	if err != nil {
		return fmt.Errorf("parse image reference: %w", err)
	}

	// Pull the image
	img, err := crane.Pull(ref.String())
	if err != nil {
		return fmt.Errorf("pull image: %w", err)
	}

	// Get the image digest
	digest, err := img.Digest()
	if err != nil {
		return fmt.Errorf("get image digest: %w", err)
	}

	m.logger.Info("image pulled", "digest", digest.String())

	// Verify image digest if provided
	if imageDigest != "" {
		if err := m.verifyImageDigest(digest, imageDigest); err != nil {
			return err
		}
	}

	// Extract the binary from the image layers
	if err := m.extractBinary(img, destination, binaryName); err != nil {
		return fmt.Errorf("extract binary: %w", err)
	}

	// Make the binary executable
	if err := os.Chmod(destination, 0755); err != nil {
		return fmt.Errorf("chmod binary: %w", err)
	}

	// Verify the actual binary checksum if provided
	if binaryChecksum != "" {
		actualChecksum, err := ComputeChecksum(destination)
		if err != nil {
			return fmt.Errorf("compute binary checksum: %w", err)
		}
		if actualChecksum != binaryChecksum {
			// Clean up the invalid binary
			os.Remove(destination)
			return fmt.Errorf("binary checksum mismatch: got %s, expected %s", actualChecksum, binaryChecksum)
		}
		m.logger.Info("binary checksum verified", "checksum", actualChecksum)
	}

	m.logger.Info("binary pulled successfully",
		"source", source,
		"destination", destination,
		"digest", digest.String())

	return nil
}

// verifyImageDigest verifies the image manifest digest matches the expected digest
func (m *Manager) verifyImageDigest(actual v1.Hash, expected string) error {
	// Expected format: "sha256:abc123..."
	parts := strings.SplitN(expected, ":", 2)
	if len(parts) != 2 {
		return fmt.Errorf("invalid digest format: %s (expected sha256:hex)", expected)
	}

	algorithm := parts[0]
	expectedHash := parts[1]

	if algorithm != "sha256" {
		return fmt.Errorf("unsupported digest algorithm: %s (only sha256 supported)", algorithm)
	}

	if actual.Hex != expectedHash {
		return fmt.Errorf("image digest mismatch: got %s, expected %s", actual.Hex, expectedHash)
	}

	m.logger.Info("image digest verified", "digest", actual.String())
	return nil
}

// extractBinary extracts the binary from the image layers
// Assumes the binary is at the root of the image filesystem or in bin/
func (m *Manager) extractBinary(img v1.Image, destination, binaryName string) error {
	layers, err := img.Layers()
	if err != nil {
		return fmt.Errorf("get layers: %w", err)
	}

	// Create destination directory if it doesn't exist
	destDir := filepath.Dir(destination)
	if err := os.MkdirAll(destDir, 0755); err != nil {
		return fmt.Errorf("create destination directory: %w", err)
	}

	// Look through layers for the binary
	for i, layer := range layers {
		m.logger.Debug("processing layer", "index", i)

		rc, err := layer.Uncompressed()
		if err != nil {
			return fmt.Errorf("uncompress layer %d: %w", i, err)
		}

		found, err := m.extractFromTar(rc, destination, binaryName)
		rc.Close()

		if err != nil {
			return fmt.Errorf("extract from layer %d: %w", i, err)
		}

		if found {
			m.logger.Debug("binary found in layer", "index", i)
			return nil
		}
	}

	return fmt.Errorf("binary '%s' not found in any layer", binaryName)
}

// extractFromTar extracts the binary from a tar stream
// Returns true if the binary was found and extracted
func (m *Manager) extractFromTar(r io.Reader, destination, binaryName string) (bool, error) {
	tr := tar.NewReader(r)

	for {
		header, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return false, fmt.Errorf("read tar: %w", err)
		}

		// Only process regular files
		if header.Typeflag != tar.TypeReg {
			continue
		}

		// Check if this is the binary we're looking for
		// Handle various path formats: /binary, ./binary, /bin/binary, ./bin/binary
		name := header.Name
		name = strings.TrimPrefix(name, "./")
		name = strings.TrimPrefix(name, "/")

		// Match if the filename matches (at root or in bin/)
		if name == binaryName || name == "bin/"+binaryName {
			m.logger.Debug("found binary in tar", "path", header.Name, "size", header.Size)

			// Use atomic swap: write to temp file, fsync, then rename
			destDir := filepath.Dir(destination)
			tempFile, err := os.CreateTemp(destDir, ".binary-*.tmp")
			if err != nil {
				return false, fmt.Errorf("create temp file: %w", err)
			}
			tempPath := tempFile.Name()

			written, err := io.Copy(tempFile, tr)
			if err != nil {
				tempFile.Close()
				os.Remove(tempPath)
				return false, fmt.Errorf("write file: %w", err)
			}

			// Sync to disk before rename
			if err := tempFile.Sync(); err != nil {
				tempFile.Close()
				os.Remove(tempPath)
				return false, fmt.Errorf("sync file: %w", err)
			}
			tempFile.Close()

			// Atomic rename
			if err := os.Rename(tempPath, destination); err != nil {
				os.Remove(tempPath)
				return false, fmt.Errorf("rename file: %w", err)
			}

			m.logger.Debug("extracted binary", "bytes", written)
			return true, nil
		}
	}

	return false, nil
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

// PullAndVerify pulls a binary and verifies it matches the expected checksum
// Returns the actual checksum of the pulled binary
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
