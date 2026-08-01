package registry

import "os"

// ShouldSkipBinaryPull decides whether to skip the OCI pull for an
// existing on-disk binary based on the configured verification fields.
//
// Decision matrix:
//   - dest not found              → pull (binary missing)
//   - binary_checksum set, match  → skip (verified)
//   - binary_checksum set, mismatch → pull (stale)
//   - binary_checksum set, compute fails → pull (can't verify)
//   - image_digest set (no checksum) → pull (must verify via OCI)
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
		return false, "image_digest pinned, must verify via pull"
	}

	return true, "no verification configured"
}
