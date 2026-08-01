package registry

import (
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"testing"
)

func newTestManager() *Manager {
	return NewManager(slog.New(slog.NewTextHandler(io.Discard, nil)), t_tempCache())
}

func t_tempCache() string { return filepath.Join(os.TempDir(), "hydra-registry-cache-test") }

// ResolveDigest must survive a process restart that skipped the pull. Without
// the sidecar fallback, /status reports an empty pulled_digest and deploy-time
// digest verification fails against a perfectly healthy node.
func TestResolveDigest(t *testing.T) {
	const source = "ghcr.io/ddvnguyen/llama-server:sm60-llama-server-latest"
	const digest = "sha256:30092e047989db36a24d7b8f74fd4126e8109a05f7db88414f6d20a8551da58b"

	t.Run("prefers in-process pull over sidecar", func(t *testing.T) {
		dest := filepath.Join(t.TempDir(), "llama-server")
		if err := RecordDigest(dest, "sha256:stale"); err != nil {
			t.Fatalf("RecordDigest: %v", err)
		}
		m := newTestManager()
		m.pulledDigest[source] = digest

		if got := m.ResolveDigest(source, dest); got != digest {
			t.Errorf("got %q, want in-process digest %q", got, digest)
		}
	})

	t.Run("falls back to sidecar when pull was skipped", func(t *testing.T) {
		dest := filepath.Join(t.TempDir(), "llama-server")
		if err := RecordDigest(dest, digest); err != nil {
			t.Fatalf("RecordDigest: %v", err)
		}
		m := newTestManager() // no in-process pull, as after a restart

		if got := m.ResolveDigest(source, dest); got != digest {
			t.Errorf("got %q, want sidecar digest %q", got, digest)
		}
	})

	t.Run("tolerates trailing newline in sidecar", func(t *testing.T) {
		dest := filepath.Join(t.TempDir(), "llama-server")
		if err := os.WriteFile(DigestSidecarPath(dest), []byte(digest+"\n"), 0o644); err != nil {
			t.Fatalf("write sidecar: %v", err)
		}
		if got := newTestManager().ResolveDigest(source, dest); got != digest {
			t.Errorf("got %q, want %q", got, digest)
		}
	})

	t.Run("empty when neither source is available", func(t *testing.T) {
		dest := filepath.Join(t.TempDir(), "llama-server")
		if got := newTestManager().ResolveDigest(source, dest); got != "" {
			t.Errorf("got %q, want empty", got)
		}
	})

	t.Run("empty destination does not panic", func(t *testing.T) {
		if got := newTestManager().ResolveDigest(source, ""); got != "" {
			t.Errorf("got %q, want empty", got)
		}
	})
}

func TestDigestSidecarPath(t *testing.T) {
	if got, want := DigestSidecarPath("/llama/bin/llama-engine"), "/llama/bin/llama-engine.digest"; got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}
