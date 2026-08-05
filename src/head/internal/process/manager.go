package process

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/config"
	hydralog "github.com/ddvnguyen/hydra_vortex/hydra-head/internal/logging"
)

type State string

const (
	StateStopped  State = "stopped"
	StateStarting State = "starting"
	StateRunning  State = "running"
	StateReady    State = "ready"
	StateSuspect  State = "suspect"
	StateStopping State = "stopping"
)

type ProcessInfo struct {
	Name         string `json:"name"`
	PID          int    `json:"pid"`
	State        State  `json:"state"`
	Ready        bool   `json:"ready"`
	Uptime       string `json:"uptime"`
	RestartCount int    `json:"restart_count"`
	LastExitCode int    `json:"last_exit_code"`
	LastError    string `json:"last_error,omitempty"`
}

type Manager struct {
	cfg       *config.Config
	logger    *slog.Logger
	otel      *hydralog.SharedLogger
	processes map[string]*managedProcess
	mu        sync.RWMutex // guards only the processes map
	ctx       context.Context
	cancel    context.CancelFunc

	// Per-name starting guards to prevent concurrent start races
	startingMu sync.Mutex
	starting   map[string]bool
}

type managedProcess struct {
	name         string
	cmd          *exec.Cmd
	state        State
	pid          int
	startedAt    time.Time
	restartCount int
	lastExitCode int
	lastError    string
	logWriter    io.Writer
	// No `childLogProvider` field. The per-child OTel
	// LoggerProvider is created once per child type
	// (llama-server / node-exporter / nvidia-exporter) and
	// reused on auto-restart (see StartLlama/StartService in
	// this file). It is NEVER shut down by stop()/restart()
	// because that would also tear down the SHARED processor
	// (the SDK's LoggerProvider.Shutdown() walks all registered
	// processors — see the second-pass PR #369 review). The
	// shared batch is flushed once at parent shutdown via
	// otelShutdown in main.go; child providers live for the
	// parent's lifetime.
	manualStop bool
	startFunc  func() error
	mu         sync.RWMutex // guards all fields below this point
	done       chan struct{}

	// Readiness (llama only): set when a sentinel line arrives on stdout.
	ready     bool
	readyAt   time.Time
	suspect   bool
	readyCh   chan struct{} // closed once ready (or when the proc exits)
	sentinels []string
}

func NewManager(cfg *config.Config, logger *slog.Logger, otel *hydralog.SharedLogger) *Manager {
	ctx, cancel := context.WithCancel(context.Background())
	return &Manager{
		cfg:       cfg,
		logger:    logger,
		otel:      otel,
		processes: make(map[string]*managedProcess),
		ctx:       ctx,
		cancel:    cancel,
		starting:  make(map[string]bool),
	}
}

// isActiveState reports whether a state means "process is alive and being
// supervised" (vs. stopped/starting/stopping). Includes ready and suspect,
// which are refinements of running for the llama process.
func isActiveState(s State) bool {
	return s == StateRunning || s == StateReady || s == StateSuspect
}

// getProcess returns the proc pointer for the given name, or nil if not found.
// Callers must use proc.mu for all field access on the returned pointer.
func (m *Manager) getProcess(name string) *managedProcess {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.processes[name]
}

// listRunning returns a snapshot of process names that are currently active
// (running / ready / suspect). Uses m.mu only for map iteration; each
// proc.state read is guarded by proc.mu.
func (m *Manager) listRunning() []string {
	m.mu.RLock()
	procs := make([]*managedProcess, 0, len(m.processes))
	for _, p := range m.processes {
		procs = append(procs, p)
	}
	m.mu.RUnlock()

	var running []string
	for _, p := range procs {
		p.mu.RLock()
		if isActiveState(p.state) {
			running = append(running, p.name)
		}
		p.mu.RUnlock()
	}
	return running
}

// childLoggerFor returns a per-child slog.Logger with the OTel
// service.name set to the child's component label. The OTel
// resource carries the parent's service.instance.id (rtx / p100)
// unchanged. Falls back to a no-op (text-only) logger if the OTel
// shared logger is not wired (e.g., in unit tests).
//
// The returned child provider wraps the *shared* processor (so
// the OTLP/HTTP connection is reused) but has its own per-child
// resource (service.name=llama-server / node-exporter /
// nvidia-exporter). It is INTENTIONALLY NEVER shut down — the
// SDK's LoggerProvider.Shutdown() walks all registered
// processors, and the shared processor must stay alive for the
// parent's lifetime. Instead, the provider is reused across
// auto-restarts (see StartLlama / StartService), so the per-child
// provider count is bounded by the number of unique child
// types (≤ 4: the parent + 3 children). The shared batch is
// flushed once at parent shutdown via otelShutdown in main.go.
//
// Component mapping (matches the Loki `component` label
// vocabulary in docs/design-direct-push-logging.md):
//   "llama"          -> "llama-server"
//   "node_exporter"  -> "node-exporter"
//   "nvidia_exporter"-> "nvidia-exporter"
//   anything else    -> "hydra-head" (the parent's component)
func (m *Manager) childLoggerFor(childName string) *slog.Logger {
	component := "hydra-head"
	switch childName {
	case "llama":
		component = "llama-server"
	case "node_exporter":
		component = "node-exporter"
	case "nvidia_exporter":
		component = "nvidia-exporter"
	}

	if m.otel == nil {
		// Fallback: text-only logger. Used by unit tests that
		// construct a Manager without an OTel shared logger.
		return slog.New(slog.NewTextHandler(io.Discard, &slog.HandlerOptions{
			Level: slog.LevelInfo,
		}))
	}

	handler, _, err := m.otel.ChildHandler(component, os.Stdout)
	if err != nil {
		// Fallback on error — log to the parent's logger so the
		// misconfiguration is visible in journalctl.
		m.logger.Error("failed to build child OTel handler; using text fallback",
			"child", childName, "component", component, "error", err)
		return slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{
			Level: slog.LevelInfo,
		}))
	}

	// Do NOT shut down the returned child provider — see the
	// comment on childLoggerFor above. The shared batch is
	// flushed once at parent shutdown via otelShutdown in main.go.
	return slog.New(handler)
}

func (m *Manager) StartLlama() error {
	// Acquire per-name starting guard to prevent concurrent starts
	m.startingMu.Lock()
	if m.starting["llama"] {
		m.startingMu.Unlock()
		return fmt.Errorf("llama-server already starting")
	}
	m.starting["llama"] = true
	m.startingMu.Unlock()

	// Ensure we release the guard when done
	defer func() {
		m.startingMu.Lock()
		m.starting["llama"] = false
		m.startingMu.Unlock()
	}()

	// Check existing proc under proc.mu (not m.mu) to avoid race with monitorProcess.
	// If a previous proc exists, we REUSE its inner logger
	// (per the auto-restart fix in #30b9257 follow-up): building
	// a new provider and then immediately discarding it (because
	// we re-use the old logWriter) leaks the new provider.
	// The child provider is NEVER shut down by stop()/restart() —
	// it lives for the parent's lifetime (see the comment on
	// childLoggerFor).
	var existingLogger *slog.Logger
	if existing := m.getProcess("llama"); existing != nil {
		existing.mu.RLock()
		if isActiveState(existing.state) {
			existing.mu.RUnlock()
			return fmt.Errorf("llama-server already running (PID %d)", existing.pid)
		}
		// Pull the old writer's inner logger so we can reuse
		// it below instead of building a fresh one that would
		// be immediately discarded.
		if cw, ok := existing.logWriter.(*childWriter); ok {
			existingLogger = cw.logger
		}
		existing.mu.RUnlock()
	}

	// Build new proc with all initialization under proc.mu. The
	// logWriter is a per-child OTel-aware childWriter (see
	// child_writer.go) — the manager already knows this is the
	// llama process, so the writer's service.name resource
	// attribute is set to "llama-server" at construction; no
	// per-line regex is needed.
	var childLogger *slog.Logger
	if existingLogger != nil {
		// Reuse the previous child's logger (avoids leaking a
		// fresh OTel LoggerProvider on every auto-restart).
		childLogger = existingLogger
	} else {
		childLogger = m.childLoggerFor("llama")
	}
	// Build the writer with an onLine hook that marks the process READY
	// the moment a lifecycle sentinel appears on stdout. This is the
	// event-driven readiness signal — the head never HTTP-polls llama.
	// If no sentinels are configured there is no readiness contract: the
	// process is considered ready once it is alive (legacy behaviour).
	readyCh := make(chan struct{})
	sentinels := m.cfg.ReadinessSentinels()
	proc := &managedProcess{
		name:      "llama",
		state:     StateStarting,
		done:      make(chan struct{}),
		ready:     len(sentinels) == 0,
		readyCh:   readyCh,
		sentinels: sentinels,
	}
	if len(sentinels) == 0 {
		// No readiness contract: assume ready once alive. Close the channel
		// so the (skipped) watcher and any caller waiting on it unblock.
		close(readyCh)
	}
	proc.logWriter = newChildWriter(childLogger, func(line string) { m.onLlamaLine(proc, line) })

	// Pre-populate state that we want to preserve from a prior run, under proc.mu.
	if existing := m.getProcess("llama"); existing != nil {
		existing.mu.RLock()
		proc.restartCount = existing.restartCount
		// logWriter was already set above (either fresh or reused).
		existing.mu.RUnlock()
	}

	// Insert into map under m.mu.
	m.mu.Lock()
	m.processes["llama"] = proc
	m.mu.Unlock()

	// Set up the exec.Cmd outside any lock (exec.Command is safe to call).
	args := m.cfg.BuildLlamaArgs()

	// Hydra #383 T4: fit preflight. If --peer-only, skip (no model to check).
	if !m.cfg.IsPeerOnly() && m.cfg.Llama.Binary != "" {
		fitBin := filepath.Join(filepath.Dir(m.cfg.Llama.Binary), "llama-fit-params")
		if _, err := os.Stat(fitBin); err == nil {
			// Build only model-fitting args — llama-fit-params only recognizes
			// model/context/device params (LLAMA_EXAMPLE_FIT_PARAMS). Server-only
			// flags (--host, --port, --cache-prompt, etc.) are excluded.
			fitArgs := m.cfg.BuildFitArgs()
			fitCmd := exec.Command(fitBin, fitArgs...)
			fitCmd.Env = os.Environ()
			for k, v := range m.cfg.Llama.Env {
				fitCmd.Env = append(fitCmd.Env, fmt.Sprintf("%s=%s", k, v))
			}
			if out, err := fitCmd.CombinedOutput(); err != nil {
				outStr := string(out)
				// n_gpu_layers already set by user → non-fatal: the user
				// explicitly configured the split/offload. Avoids the
				// "n_gpu_layers already set to N, abort" dead-end that
				// happens when CPU offload (n-cpu-moe / override-tensor)
				// makes the model fit despite n_gpu_layers being "too high".
				if strings.Contains(outStr, "n_gpu_layers already set by user") {
					slog.Warn("fit preflight: n_gpu_layers already set by user, continuing", "output", outStr)
				} else {
					m.mu.Lock()
					if proc, ok := m.processes["llama"]; ok {
						proc.mu.Lock()
						proc.state = StateStopped
						proc.lastError = fmt.Sprintf("fit preflight failed: %s", outStr)
						proc.mu.Unlock()
						close(proc.done)
					}
					m.mu.Unlock()
					return fmt.Errorf("fit preflight failed: %s: %s", err, outStr)
				}
			}
		}
	}

	cmd := exec.Command(m.cfg.Llama.Binary, args...)
	cmd.Dir = m.cfg.Llama.WorkingDir

	// All proc field writes are under proc.mu.
	proc.mu.Lock()
	cmd.Stdout = proc.logWriter
	cmd.Stderr = proc.logWriter
	cmd.Env = os.Environ()
	for k, v := range m.cfg.Llama.Env {
		cmd.Env = append(cmd.Env, fmt.Sprintf("%s=%s", k, v))
	}
	proc.cmd = cmd
	proc.startFunc = func() error { return m.StartLlama() }
	proc.mu.Unlock()

	if err := cmd.Start(); err != nil {
		proc.mu.Lock()
		proc.state = StateStopped
		proc.lastError = err.Error()
		proc.mu.Unlock()
		close(proc.done)
		return fmt.Errorf("start llama-server: %w", err)
	}

	proc.mu.Lock()
	proc.pid = cmd.Process.Pid
	proc.state = StateRunning
	proc.startedAt = time.Now()
	proc.mu.Unlock()

	m.logger.Info("llama-server started", "pid", proc.pid, "args", args)

	go m.monitorProcess(proc)
	go m.watchReadiness(proc)

	return nil
}

// onLlamaLine is the childWriter onLine hook for the llama process. It
// matches each stdout line against the configured readiness sentinels and
// marks the process READY on the first hit. Idempotent.
func (m *Manager) onLlamaLine(proc *managedProcess, line string) {
	proc.mu.RLock()
	alreadyReady := proc.ready
	proc.mu.RUnlock()
	if alreadyReady {
		return
	}
	for _, sentinel := range proc.sentinels {
		if strings.Contains(line, sentinel) {
			proc.mu.Lock()
			proc.ready = true
			proc.readyAt = time.Now()
			proc.state = StateReady
			close(proc.readyCh)
			proc.mu.Unlock()
			m.logger.Info("llama-engine ready (stdout sentinel)",
				"pid", proc.pid, "sentinel", sentinel)
			return
		}
	}
}

// watchReadiness watches for the readiness sentinel with a miss-deadline.
// If the sentinel never arrives within the configured timeout, the process
// is marked SUSPECT (started, not ready) so the coordinator sees it is not
// serving — but the head does NOT restart it here: a slow model load on a
// VM disk is legitimate, and a real crash is handled by monitorProcess via
// the exit event. Returns when ready, suspect, or the process exits.
func (m *Manager) watchReadiness(proc *managedProcess) {
	if len(proc.sentinels) == 0 {
		// No readiness contract (legacy): the process is ready once alive.
		// Keep state=Running and leave proc.ready=true from StartLlama.
		<-proc.done
		return
	}
	timeout := m.cfg.ReadinessTimeout()
	timer := time.NewTimer(timeout)
	defer timer.Stop()

	select {
	case <-proc.readyCh:
		// ready — nothing to do, onLlamaLine already set the state.
	case <-proc.done:
		// process exited before readiness (monitorProcess owns the state).
	case <-m.ctx.Done():
	case <-timer.C:
		proc.mu.Lock()
		if !proc.ready && proc.state == StateRunning {
			proc.state = StateSuspect
			proc.suspect = true
		}
		proc.mu.Unlock()
		m.logger.Warn("llama-engine readiness sentinel not observed within timeout",
			"pid", proc.pid, "timeout", timeout, "sentinels", proc.sentinels)
	}
}

func (m *Manager) monitorProcess(proc *managedProcess) {
	defer close(proc.done)

	err := proc.cmd.Wait()

	// Update state under proc.mu — this is the sole writer of the exit fields
	// and must be synchronized with any reader of state/manualStop.
	proc.mu.Lock()
	proc.state = StateStopped
	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			proc.lastExitCode = exitErr.ExitCode()
		} else {
			proc.lastError = err.Error()
		}
	} else {
		proc.lastExitCode = 0
	}
	manualStop := proc.manualStop
	proc.mu.Unlock()

	m.logger.Warn("process exited",
		"name", proc.name,
		"pid", proc.pid,
		"exit_code", proc.lastExitCode,
		"error", proc.lastError)

	if !manualStop && m.shouldRestart(proc) {
		go m.restartWithBackoff(proc)
	}
}

func (m *Manager) shouldRestart(proc *managedProcess) bool {
	select {
	case <-m.ctx.Done():
		return false
	default:
		return true
	}
}

func (m *Manager) restartWithBackoff(proc *managedProcess) {
	backoff := time.Second
	maxBackoff := 30 * time.Second

	for {
		select {
		case <-m.ctx.Done():
			return
		case <-time.After(backoff):
			m.logger.Info("attempting restart",
				"name", proc.name,
				"attempt", proc.restartCount+1,
				"backoff", backoff)

			// For the llama process, wait for its HTTP port to be free
			// before attempting restart to avoid "couldn't bind" crash loop.
			if proc.name == "llama" {
				m.waitForPortRelease(m.cfg.Llama.Port, 10*time.Second)
			}

			proc.mu.Lock()
			proc.restartCount++
			proc.mu.Unlock()

			var err error
			if proc.startFunc != nil {
				err = proc.startFunc()
			} else {
				err = fmt.Errorf("no start function for process %s", proc.name)
			}
			if err != nil {
				m.logger.Error("restart failed", "name", proc.name, "error", err)

				backoff *= 2
				if backoff > maxBackoff {
					backoff = maxBackoff
				}
				continue
			}

			m.logger.Info("restart successful", "name", proc.name)
			return
		}
	}
}

func (m *Manager) StopLlama() error {
	return m.stop("llama", true)
}

func (m *Manager) RestartLlama() error {
	if err := m.StopLlama(); err != nil {
		m.logger.Warn("stop failed, starting anyway", "error", err)
	}
	// Wait for the engine's HTTP port to be fully released before starting.
	// The old process may hold the port in TIME_WAIT after SIGTERM. Without
	// this, the new process hits "couldn't bind HTTP server socket" and
	// terminates immediately, creating a crash loop.
	m.waitForPortRelease(m.cfg.Llama.Port, 15*time.Second)
	return m.StartLlama()
}

// waitForPortRelease polls until the given TCP port is no longer in LISTEN
// state, up to maxWait. Returns once the port is free or the timeout expires.
func (m *Manager) waitForPortRelease(port int, maxWait time.Duration) {
	deadline := time.Now().Add(maxWait)
	for time.Now().Before(deadline) {
		if !isPortListening(port) {
			return
		}
		time.Sleep(500 * time.Millisecond)
	}
	m.logger.Warn("port still in use after wait, proceeding anyway",
		"port", port, "wait", maxWait)
}

func isPortListening(port int) bool {
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), 200*time.Millisecond)
	if err != nil {
		return false
	}
	conn.Close()
	return true
}

func (m *Manager) StartService(name string) error {
	svc := m.cfg.ServiceConfig(name)
	if !svc.Enabled {
		return nil
	}

	// Acquire per-name starting guard to prevent concurrent starts
	m.startingMu.Lock()
	if m.starting[name] {
		m.startingMu.Unlock()
		return fmt.Errorf("%s already starting", name)
	}
	m.starting[name] = true
	m.startingMu.Unlock()

	// Ensure we release the guard when done
	defer func() {
		m.startingMu.Lock()
		m.starting[name] = false
		m.startingMu.Unlock()
	}()

	// Check existing proc under proc.mu. If a previous proc
	// exists, we REUSE its inner logger (per the auto-restart
	// fix in #30b9257 follow-up): building a new provider and
	// then immediately discarding it (because we re-use the old
	// logWriter) leaks the new provider.
	// The child provider is NEVER shut down by stop()/restart() —
	// it lives for the parent's lifetime (see the comment on
	// childLoggerFor).
	var existingLogger *slog.Logger
	if existing := m.getProcess(name); existing != nil {
		existing.mu.RLock()
		if isActiveState(existing.state) {
			existing.mu.RUnlock()
			return fmt.Errorf("%s already running (PID %d)", name, existing.pid)
		}
		// Pull the old writer's inner logger so we can reuse
		// it below instead of building a fresh one that would
		// be immediately discarded.
		if cw, ok := existing.logWriter.(*childWriter); ok {
			existingLogger = cw.logger
		}
		existing.mu.RUnlock()
	}

	// Build new proc with initial state under proc.mu. The
	// logWriter is a per-child childWriter (see child_writer.go)
	// with service.name set to the child's component label
	// (e.g., "node-exporter", "nvidia-exporter") at construction;
	// no per-line regex is needed.
	var childLogger *slog.Logger
	if existingLogger != nil {
		// Reuse the previous child's logger (avoids leaking a
		// fresh OTel LoggerProvider on every auto-restart).
		childLogger = existingLogger
	} else {
		childLogger = m.childLoggerFor(name)
	}
	proc := &managedProcess{
		name:      name,
		state:     StateStarting,
		logWriter: newChildWriter(childLogger),
		done:      make(chan struct{}),
	}

	// Preserve restart count if a prior instance exists.
	if existing := m.getProcess(name); existing != nil {
		existing.mu.RLock()
		proc.restartCount = existing.restartCount
		// logWriter was already set above (either fresh or reused).
		existing.mu.RUnlock()
	}

	// Insert into map under m.mu.
	m.mu.Lock()
	m.processes[name] = proc
	m.mu.Unlock()

	binary := m.cfg.ServiceBinary(name)
	if binary == "" {
		return fmt.Errorf("no binary configured for %s", name)
	}

	// Set up exec.Cmd outside the proc.mu (safe; cmd is unexported field).
	args := m.cfg.BuildServiceArgs(name, svc)
	cmd := exec.Command(binary, args...)

	// All field writes go through proc.mu.
	proc.mu.Lock()
	cmd.Stdout = proc.logWriter
	cmd.Stderr = proc.logWriter
	proc.cmd = cmd
	proc.startFunc = func() error { return m.StartService(name) }
	proc.mu.Unlock()

	if err := cmd.Start(); err != nil {
		proc.mu.Lock()
		proc.state = StateStopped
		proc.lastError = err.Error()
		proc.mu.Unlock()
		close(proc.done)
		return fmt.Errorf("start %s: %w", name, err)
	}

	proc.mu.Lock()
	proc.pid = cmd.Process.Pid
	proc.state = StateRunning
	proc.startedAt = time.Now()
	proc.mu.Unlock()

	m.logger.Info("service started", "name", name, "pid", proc.pid, "binary", binary, "args", args)

	go m.monitorProcess(proc)

	return nil
}

func (m *Manager) StopService(name string) error {
	return m.stop(name, false)
}

// stop is the shared stop implementation for both llama and sub-services.
// All state/manualStop/pid writes go through proc.mu, never m.mu.
func (m *Manager) stop(name string, isLlama bool) error {
	proc := m.getProcess(name)
	if proc == nil {
		if isLlama {
			return fmt.Errorf("llama-server not running")
		}
		return fmt.Errorf("%s not running", name)
	}

	// Mark as stopping + manual under proc.mu — the only writer of these
	// fields outside monitorProcess.
	proc.mu.Lock()
	if !isActiveState(proc.state) {
		proc.mu.Unlock()
		if isLlama {
			return fmt.Errorf("llama-server not running")
		}
		return fmt.Errorf("%s not running", name)
	}
	proc.state = StateStopping
	proc.manualStop = true
	proc.mu.Unlock()

	m.logger.Info("stopping service", "name", name, "pid", proc.pid)

	if err := proc.cmd.Process.Signal(syscall.SIGTERM); err != nil {
		return fmt.Errorf("send SIGTERM to %s: %w", name, err)
	}

	// Wait for the process to exit, with a hard-kill fallback. The done
	// channel is closed by monitorProcess, so we never race with cmd.Wait().
	select {
	case <-time.After(10 * time.Second):
		m.logger.Warn("service did not exit gracefully, killing", "name", name, "pid", proc.pid)
		if err := proc.cmd.Process.Kill(); err != nil {
			return fmt.Errorf("kill %s: %w", name, err)
		}
		<-proc.done
	case <-proc.done:
		// process exited cleanly
	}

	// Order matters: flush the child writer's remaining
	// buffered bytes FIRST (while the OTel LoggerProvider is
	// still alive). We do NOT shut down the child OTel
	// LoggerProvider here — the SDK's LoggerProvider.Shutdown()
	// walks all registered processors, and the child provider
	// uses the *shared* processor. Shutting it down would tear
	// down the shared batch, breaking the parent and all other
	// children. The shared batch is flushed once at parent
	// shutdown via otelShutdown in main.go; the child providers
	// (one per unique child type, ≤ 4) live for the parent's
	// lifetime. See the second-pass PR #369 review for the
	// details.
	proc.mu.Lock()
	proc.state = StateStopped
	proc.pid = 0
	proc.mu.Unlock()

	// Flush the child writer's remaining buffered bytes (a
	// final log line without a trailing newline on a crash).
	if cw, ok := proc.logWriter.(*childWriter); ok {
		if err := cw.Close(); err != nil {
			m.logger.Warn("child writer close failed",
				"name", name, "error", err)
		}
	}

	return nil
}

func (m *Manager) RestartService(name string) error {
	if err := m.StopService(name); err != nil {
		m.logger.Warn("stop failed, starting anyway", "name", name, "error", err)
	}
	time.Sleep(time.Second)
	return m.StartService(name)
}

func (m *Manager) GetProcessInfo(name string) (*ProcessInfo, error) {
	proc := m.getProcess(name)
	if proc == nil {
		return nil, fmt.Errorf("process %s not found", name)
	}

	proc.mu.RLock()
	defer proc.mu.RUnlock()

	info := &ProcessInfo{
		Name:         proc.name,
		PID:          proc.pid,
		State:        proc.state,
		Ready:        proc.ready,
		RestartCount: proc.restartCount,
		LastExitCode: proc.lastExitCode,
		LastError:    proc.lastError,
	}

	if (proc.state == StateRunning || proc.state == StateReady || proc.state == StateSuspect) && !proc.startedAt.IsZero() {
		info.Uptime = time.Since(proc.startedAt).Round(time.Second).String()
	}

	return info, nil
}

func (m *Manager) GetAllProcessInfo() map[string]*ProcessInfo {
	// Snapshot the map under m.mu, then read each proc's state under its own lock.
	m.mu.RLock()
	procs := make([]*managedProcess, 0, len(m.processes))
	for _, p := range m.processes {
		procs = append(procs, p)
	}
	m.mu.RUnlock()

	result := make(map[string]*ProcessInfo, len(procs))
	for _, p := range procs {
		info, err := m.GetProcessInfo(p.name)
		if err == nil {
			result[p.name] = info
		}
	}
	return result
}

func (m *Manager) Shutdown() {
	m.logger.Info("shutting down process manager")
	m.cancel()

	for _, name := range m.listRunning() {
		m.logger.Info("stopping process", "name", name)
		if name == "llama" {
			if err := m.StopLlama(); err != nil {
				m.logger.Error("failed to stop llama", "error", err)
			}
		} else {
			if err := m.StopService(name); err != nil {
				m.logger.Error("failed to stop service", "name", name, "error", err)
			}
		}
	}
}
