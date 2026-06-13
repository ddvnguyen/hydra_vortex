package registry

import (
	"os"
	"path/filepath"
	"testing"
)

func TestComputeChecksum(t *testing.T) {
	tmpDir := t.TempDir()
	testFile := filepath.Join(tmpDir, "test.txt")

	content := []byte("hello world")
	if err := os.WriteFile(testFile, content, 0644); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	checksum, err := ComputeChecksum(testFile)
	if err != nil {
		t.Fatalf("ComputeChecksum failed: %v", err)
	}

	expected := "sha256:b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"
	if checksum != expected {
		t.Errorf("Checksum mismatch: got %s, expected %s", checksum, expected)
	}
}
