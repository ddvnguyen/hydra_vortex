package registry

import (
	"os"
	"path/filepath"
	"testing"
)

func TestShouldSkipBinaryPull(t *testing.T) {
	// Create a temp dir with a known file for checksum-based tests.
	tmpDir := t.TempDir()
	knownFile := filepath.Join(tmpDir, "binary")
	if err := os.WriteFile(knownFile, []byte("hello world"), 0644); err != nil {
		t.Fatalf("setup: write known file: %v", err)
	}
	knownChecksum, err := ComputeChecksum(knownFile)
	if err != nil {
		t.Fatalf("setup: compute checksum: %v", err)
	}

	// A different file to produce a mismatch.
	mismatchFile := filepath.Join(tmpDir, "other")
	if err := os.WriteFile(mismatchFile, []byte("goodbye world"), 0644); err != nil {
		t.Fatalf("setup: write mismatch file: %v", err)
	}

	// A file with a recorded digest sidecar (what a successful pull leaves
	// behind via RecordDigest).
	sidecarFile := filepath.Join(tmpDir, "sidecar-verified")
	if err := os.WriteFile(sidecarFile, []byte("verified bytes"), 0644); err != nil {
		t.Fatalf("setup: write sidecarFile: %v", err)
	}
	const pinnedDigest = "sha256:abcdef1234567890"
	if err := os.WriteFile(DigestSidecarPath(sidecarFile), []byte(pinnedDigest), 0644); err != nil {
		t.Fatalf("setup: write digest sidecar: %v", err)
	}

	missingPath := filepath.Join(tmpDir, "does-not-exist")

	tests := []struct {
		name           string
		dest           string
		binaryChecksum string
		imageDigest    string
		wantSkip       bool
		wantReason     string
	}{
		{
			name:       "file not found",
			dest:       missingPath,
			wantSkip:   false,
			wantReason: "not found",
		},
		{
			name:           "checksum match",
			dest:           knownFile,
			binaryChecksum: knownChecksum,
			wantSkip:       true,
			wantReason:     "checksum match",
		},
		{
			name:           "checksum mismatch",
			dest:           knownFile,
			binaryChecksum: "sha256:0000000000000000000000000000000000000000000000000000000000000000",
			wantSkip:       false,
			wantReason:     "checksum mismatch",
		},
		{
			name:        "image_digest set, no checksum",
			dest:        knownFile,
			imageDigest: "sha256:abcdef1234567890",
			wantSkip:    false,
			wantReason:  "image_digest pinned, must verify via pull",
		},
		{
			name:        "image_digest matches recorded sidecar",
			dest:        sidecarFile,
			imageDigest: pinnedDigest,
			wantSkip:    true,
			wantReason:  "image_digest matches recorded sidecar",
		},
		{
			name:        "image_digest mismatches recorded sidecar",
			dest:        sidecarFile,
			imageDigest: "sha256:deadbeefdeadbeef",
			wantSkip:    false,
			wantReason:  "image_digest pinned, must verify via pull",
		},
		{
			name:       "neither set",
			dest:       knownFile,
			wantSkip:   true,
			wantReason: "no verification configured",
		},
		{
			name:           "checksum mismatch uses different file",
			dest:           mismatchFile,
			binaryChecksum: knownChecksum,
			wantSkip:       false,
			wantReason:     "checksum mismatch",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gotSkip, gotReason := ShouldSkipBinaryPull(tt.dest, tt.binaryChecksum, tt.imageDigest)
			if gotSkip != tt.wantSkip {
				t.Errorf("skip = %v, want %v", gotSkip, tt.wantSkip)
			}
			if gotReason != tt.wantReason {
				t.Errorf("reason = %q, want %q", gotReason, tt.wantReason)
			}
		})
	}
}
