package process

import (
	"bytes"
	"context"
	"io"
	"log/slog"
	"strings"
	"sync"
	"testing"
)

// captureHandler is a minimal slog.Handler that captures every
// record's message into a slice. It is used to assert what the
// childWriter emitted.
type captureHandler struct {
	mu       sync.Mutex
	messages []string
}

func (h *captureHandler) Enabled(_ context.Context, _ slog.Level) bool { return true }

func (h *captureHandler) Handle(_ context.Context, r slog.Record) error {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.messages = append(h.messages, r.Message)
	return nil
}

func (h *captureHandler) WithAttrs(_ []slog.Attr) slog.Handler { return h }
func (h *captureHandler) WithGroup(_ string) slog.Handler      { return h }

func (h *captureHandler) Messages() []string {
	h.mu.Lock()
	defer h.mu.Unlock()
	out := make([]string, len(h.messages))
	copy(out, h.messages)
	return out
}

func TestChildWriterEmitsCompleteLines(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	// Three complete lines in one Write call.
	in := []byte("first line\nsecond line\nthird line\n")
	n, err := w.Write(in)
	if err != nil {
		t.Fatalf("Write: %v", err)
	}
	if n != len(in) {
		t.Errorf("expected to write %d bytes, wrote %d", len(in), n)
	}

	got := buf.String()
	for _, want := range []string{"first line", "second line", "third line"} {
		if !strings.Contains(got, want) {
			t.Errorf("expected output to contain %q, got %q", want, got)
		}
	}
	// Newlines should be stripped.
	if strings.Contains(got, "first line\n") {
		t.Errorf("expected newline to be stripped; got %q", got)
	}
}

func TestChildWriterBuffersPartialLine(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	// First Write: partial line, no newline.
	if _, err := w.Write([]byte("partial ")); err != nil {
		t.Fatalf("Write 1: %v", err)
	}
	// The partial line should NOT have been emitted yet.
	if strings.Contains(buf.String(), "partial") {
		t.Errorf("partial line emitted prematurely: %q", buf.String())
	}

	// Second Write: completes the line.
	if _, err := w.Write([]byte("rest of line\n")); err != nil {
		t.Fatalf("Write 2: %v", err)
	}
	if !strings.Contains(buf.String(), "partial rest of line") {
		t.Errorf("expected concatenated line in output, got %q", buf.String())
	}
}

func TestChildWriterStripsCarriageReturn(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	// Windows-style line ending: \r\n
	if _, err := w.Write([]byte("with CR\r\n")); err != nil {
		t.Fatalf("Write: %v", err)
	}
	if strings.Contains(buf.String(), "with CR\r") {
		t.Errorf("expected CR to be stripped, got %q", buf.String())
	}
}

func TestChildWriterIsAnIOWriter(t *testing.T) {
	// Compile-time interface check (the package also has an
	// explicit `var _ io.Writer = (*childWriter)(nil)` assertion).
	var _ io.Writer = (*childWriter)(nil)
}

// TestChildWriterCloseFlushesTrailingPartialLine covers the
// trailing-partial-line fix from PR #369 review: a child that
// exits after writing a final line without a trailing newline
// (common for crash/abort messages) would otherwise leave that
// line in w.buf forever. The Close() method must emit it as
// a final log record.
func TestChildWriterCloseFlushesTrailingPartialLine(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	// Write a partial line (no trailing newline).
	if _, err := w.Write([]byte("final crash message")); err != nil {
		t.Fatalf("Write: %v", err)
	}
	// Buffer has the partial line but no emit yet.
	if got := buf.String(); got != "" {
		t.Errorf("partial line emitted prematurely: %q", got)
	}

	// Close should flush it.
	if err := w.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	got := buf.String()
	if !strings.Contains(got, "final crash message") {
		t.Errorf("Close should have emitted the trailing partial line; got %q", got)
	}
}

// TestChildWriterCloseIsIdempotent ensures that calling Close
// twice does not panic and returns nil.
func TestChildWriterCloseIsIdempotent(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	if _, err := w.Write([]byte("hello\n")); err != nil {
		t.Fatalf("Write: %v", err)
	}
	if err := w.Close(); err != nil {
		t.Errorf("first Close: %v", err)
	}
	if err := w.Close(); err != nil {
		t.Errorf("second Close: %v", err)
	}
}

// TestChildWriterCloseHandlesCarriageReturn ensures that a
// trailing \r (from Windows-style \r\n line endings) is
// stripped before emission.
func TestChildWriterCloseHandlesCarriageReturn(t *testing.T) {
	var buf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&buf, &slog.HandlerOptions{Level: slog.LevelInfo}))
	w := newChildWriter(logger)

	if _, err := w.Write([]byte("trailing-CR\r")); err != nil {
		t.Fatalf("Write: %v", err)
	}
	if err := w.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	if strings.Contains(buf.String(), "trailing-CR\r") {
		t.Errorf("trailing \\r should have been stripped; got %q", buf.String())
	}
	if !strings.Contains(buf.String(), "trailing-CR") {
		t.Errorf("expected the line emitted without trailing \\r; got %q", buf.String())
	}
}
