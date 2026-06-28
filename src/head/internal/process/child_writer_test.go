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
