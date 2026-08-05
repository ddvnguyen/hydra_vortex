package registry

import (
	"os"
	"strings"
)

// ShouldSkipBinaryPull decides whether to skip the OCI pull for an
// existing on-disk binary based on the configured verification fields.
//
// Decision matrix:
//   - dest not found              → pull (binary missing)
//   - binary_checksum set, match  → skip (verified)
//   - binary_checksum set, mismatch → pull (stale)
//   - binary_checksum set, compute fails → pull (can't verify)
//   - image_digest set (no checksum) → skip if the recorded digest sidecar
//     matches (the binary was verified against this exact digest when pulled);
//     otherwise pull (must verify via OCI)
//   - neither set                 → skip (baked-in sidecar, unverified)
func ShouldSkipBinaryPull(dest string, binaryChecksum, imageDigest string) (skip bool, reason string) {
	if _, err := os.Stat(dest); err != nil {
		return false, "not found"
	}

	if binaryChecksum != "" {
		actual, err := ComputeChecksum(dest)
		if err != nil {
			return false, "checksum computation failed: " + err.Error()
		}
		if actual == binaryChecksum {
			return true, "checksum match"
		}
		return false, "checksum mismatch"
	}

	if imageDigest != "" {
		// A successful pull already verified this digest and recorded it in
		// the sidecar next to the binary (see RecordDigest). If the sidecar
		// matches the pinned digest, the binary on disk IS that exact image —
		// re-pulling the whole image just to re-verify is wasted bandwidth
		// (the sm60 image is ~4 GB). Only pull when the sidecar disagrees.
		if recorded, err := os.ReadFile(DigestSidecarPath(dest)); err == nil {
			if strings.TrimSpace(string(recorded)) == imageDigest {
				return true, "image_digest matches recorded sidecar"
			}
		}
		return false, "image_digest pinned, must verify via pull"
	}

	return true, "no verification configured"
}
