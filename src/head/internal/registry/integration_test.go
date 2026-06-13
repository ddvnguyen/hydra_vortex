//go:build integration
// +build integration

package registry

import (
	"log/slog"
	"os"
	"path/filepath"
	"testing"
)

func TestPullBinary(t *testing.T) {
	// Create a temp directory for the test
	tmpDir := t.TempDir()
	destination := filepath.Join(tmpDir, "[")

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(logger, tmpDir)

	// Pull busybox from Docker Hub and extract bin/[ (the actual busybox binary)
	source := "index.docker.io/library/busybox:latest"

	err := mgr.PullBinary(source, destination, "", "[")
	if err != nil {
		t.Fatalf("PullBinary failed: %v", err)
	}

	// Verify the binary exists and has content
	info, err := os.Stat(destination)
	if err != nil {
		t.Fatalf("Binary not found: %v", err)
	}

	if info.Size() == 0 {
		t.Error("Binary is empty")
	}

	t.Logf("Successfully pulled binary to %s (size: %d bytes)", destination, info.Size())
}

func TestPullBinaryWithChecksum(t *testing.T) {
	tmpDir := t.TempDir()
	destination := filepath.Join(tmpDir, "[")

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(logger, tmpDir)

	// Pull busybox first without checksum to get the actual image digest
	source := "index.docker.io/library/busybox:latest"
	err := mgr.PullBinary(source, destination, "", "[")
	if err != nil {
		t.Fatalf("Initial pull failed: %v", err)
	}

	// The image digest is what we verify, not the file checksum
	// In a real scenario, you'd get this from the registry or documentation
	// For this test, we'll just verify that wrong checksums are rejected
	imageDigest := "sha256:1cfa4e2b09e127b9c4ed43578d3f3c18e7d44ea47b9ea98475c0cbe9086525f8"

	// Pull again with the correct image digest
	destination2 := filepath.Join(tmpDir, "[2")
	err = mgr.PullBinary(source, destination2, imageDigest, "[")
	if err != nil {
		t.Fatalf("PullBinary with checksum failed: %v", err)
	}

	// Verify the file was extracted
	info, err := os.Stat(destination2)
	if err != nil {
		t.Fatalf("Binary not found: %v", err)
	}
	if info.Size() == 0 {
		t.Error("Binary is empty")
	}

	// Try with wrong checksum
	destination3 := filepath.Join(tmpDir, "[3")
	err = mgr.PullBinary(source, destination3, "sha256:0000000000000000000000000000000000000000000000000000000000000000", "[")
	if err == nil {
		t.Error("Expected error with wrong checksum, got nil")
	}
	t.Logf("Correctly rejected wrong checksum: %v", err)
}

func TestPullAndVerify(t *testing.T) {
	tmpDir := t.TempDir()
	destination := filepath.Join(tmpDir, "[")

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(logger, tmpDir)

	source := "index.docker.io/library/busybox:latest"

	// Pull and verify (without expected checksum, just returns actual)
	actualChecksum, err := mgr.PullAndVerify(source, destination, "", "[")
	if err != nil {
		t.Fatalf("PullAndVerify failed: %v", err)
	}

	if actualChecksum == "" {
		t.Error("Expected non-empty checksum")
	}

	t.Logf("Pulled and verified, checksum: %s", actualChecksum)

	// Verify the binary exists
	if _, err := os.Stat(destination); err != nil {
		t.Errorf("Binary not found after pull: %v", err)
	}
}
