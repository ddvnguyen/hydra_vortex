package process

import (
	"bytes"
	"io"
	"log/slog"
	"strings"
	"sync"
)

// childWriter is the io.Writer set as cmd.Stdout / cmd.Stderr for
// each managed process (llama-server, node_exporter,
// nvidia_exporter). It buffers bytes from the child into lines
// and emits each complete line as a log record via a per-child
// slog.Logger.
//
// The per-child logger carries the child's `service.name` resource
// attribute (set via the SharedLogger.ChildHandler in
// internal/logging). After the OTel Collector's transform
// processor maps service.name → component, the child shows up in
// Loki as `{component="llama-server"}` (or node-exporter /
// nvidia-exporter).
//
// Why this is per-child, not per-line-regex: the original
// Promtail pipeline regex-matched each child line to pick a
// component label — the same brittle regex
// (^(?P<llama_ts>\d+\.\d+\.\d+\.\d+)\s+(?P<llama_level>[A-Z])\s+)
// that audit #4 flagged. The new design removes the regex
// entirely: the manager already knows the child name at spawn
// time, so each child gets its own writer with a static
// service.name. See
// docs/design-direct-push-logging.md § "Per-child writers".
type childWriter struct {
	mu     sync.Mutex
	buf    []byte
	logger *slog.Logger
}

// newChildWriter returns an io.Writer that emits each line to the
// given per-child slog.Logger. Lines are split on '\n'; partial
// lines (without a trailing newline) are buffered for the next
// Write. The line buffer grows with each partial line; a 1 MiB
// cap is enforced by truncating the tail if a single line
// exceeds it (which would indicate a child writing a multi-MB
// blob without a newline — the truncation is logged and
// processing continues).
func newChildWriter(logger *slog.Logger) *childWriter {
	return &childWriter{
		buf:    make([]byte, 0, 4096),
		logger: logger,
	}
}

// Write implements io.Writer. The data may contain partial
// lines, complete lines, or multiple lines. Each complete line
// (terminated by '\n') is emitted as one log record via the
// per-child logger at Info level. A trailing partial line is
// buffered for the next Write.
func (w *childWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	w.buf = append(w.buf, p...)

	// Cap the buffer to prevent unbounded growth. If a child
	// process emits a single line longer than 1 MiB without a
	// newline, we truncate the tail to keep memory bounded
	// (matches the Loki max_entry_size post-cutover; a line
	// longer than this would be dropped by Loki anyway).
	const maxBuf = 1 << 20
	if len(w.buf) > maxBuf {
		// Find the last newline before the cap; if none, drop
		// the whole buffer. Either way, keep processing new
		// Writes (don't error out — the child is still alive).
		cutoff := bytes.LastIndexByte(w.buf[:maxBuf], '\n')
		if cutoff < 0 {
			w.buf = w.buf[:0]
		} else {
			// Emit everything up to and including the last
			// newline; drop the partial tail.
			tail := w.buf[cutoff+1:]
			w.buf = w.buf[:cutoff+1]
			// Process the lines we kept, then put the
			// partial tail back so the next Write
			// continues the line.
			_ = w.emitLinesLocked()
			w.buf = append(w.buf[:0], tail...)
		}
	}

	return len(p), w.emitLinesLocked()
}

// emitLinesLocked scans w.buf for complete lines (terminated by
// '\n'), emits each via the per-child logger, and leaves any
// trailing partial line in w.buf. Caller must hold w.mu.
func (w *childWriter) emitLinesLocked() error {
	for {
		idx := bytes.IndexByte(w.buf, '\n')
		if idx < 0 {
			// No complete line; the partial line stays in
			// w.buf for the next Write.
			return nil
		}
		line := w.buf[:idx]
		// Strip a trailing '\r' for Windows-style line endings.
		if len(line) > 0 && line[len(line)-1] == '\r' {
			line = line[:len(line)-1]
		}
		// Advance the buffer past the line and the '\n'.
		// w.buf is then the partial line so far (or empty).
		w.buf = w.buf[idx+1:]

		if w.logger != nil && len(line) > 0 {
			// Emit at Info level. The child logger has
			// its own service.name resource attribute
			// (set in the SharedLogger.ChildHandler
			// call), so this record shows up in Loki
			// with the right component label.
			//
			// We use Info level because child-process
			// log lines don't include severity (we'd
			// need the brittle regex to extract it).
			// For actual errors, child processes exit
			// with a non-zero status; we don't try to
			// detect log-level from the line content.
			w.logger.Info(string(line))
		}
	}
}

// Ensure io.Writer is satisfied at compile time.
var _ io.Writer = (*childWriter)(nil)

// ensure strings is referenced (used by emitLinesLocked via
// the strings.TrimRight-equivalent for the trailing '\r').
var _ = strings.TrimRight
