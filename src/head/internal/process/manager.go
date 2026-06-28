package process

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
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
	StateStopping State = "stopping"
)

type ProcessInfo struct {
	Name         string `json:"name"`
	PID          int    `json:"pid"`
	State        State  `json:"state"`
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
	// childLogProvider is the OTel LoggerProvider that the
	// childWriter uses. It wraps the *shared* processor (so the
	// OTLP connection is reused) but has its own per-child
	// resource (service.name=llama-server / node-exporter /
	// nvidia-exporter). On stop/restart we Shutdown the
	// provider to flush the in-process batch and release the
	// queue — but we do NOT touch the shared processor, which
	// is shared across all children + the parent.
	childLogProvider func() error
	manualStop   bool
	startFunc    func() error
	mu           sync.RWMutex // guards all fields below this point
	done         chan struct{}
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

// getProcess returns the proc pointer for the given name, or nil if not found.
// Callers must use proc.mu for all field access on the returned pointer.
func (m *Manager) getProcess(name string) *managedProcess {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.processes[name]
}

// listRunning returns a snapshot of process names that are currently in StateRunning.
// Uses m.mu only for map iteration; each proc.state read is guarded by proc.mu.
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
		if p.state == StateRunning {
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
// The returned *sdklog.LoggerProvider wraps the *shared*
// processor — the caller is responsible for calling Shutdown on
// it when the child stops/restarts, to flush the in-process batch
// and release the queue. The shared processor is NOT torn down
// here; it lives for the lifetime of the parent process.
//
// Component mapping (matches the Loki `component` label
// vocabulary in docs/design-direct-push-logging.md):
//   "llama"          -> "llama-server"
//   "node_exporter"  -> "node-exporter"
//   "nvidia_exporter"-> "nvidia-exporter"
//   anything else    -> "hydra-head" (the parent's component)
func (m *Manager) childLoggerFor(childName string) (*slog.Logger, func() error) {
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
		noop := slog.New(slog.NewTextHandler(io.Discard, &slog.HandlerOptions{
			Level: slog.LevelInfo,
		}))
		return noop, func() error { return nil }
	}

	handler, childProvider, err := m.otel.ChildHandler(component, os.Stdout)
	if err != nil {
		// Fallback on error — log to the parent's logger so the
		// misconfiguration is visible in journalctl.
		m.logger.Error("failed to build child OTel handler; using text fallback",
			"child", childName, "component", component, "error", err)
		fb := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{
			Level: slog.LevelInfo,
		}))
		return fb, func() error { return nil }
	}

	// Shutdown the child provider to flush the in-process batch
	// and release the queue. The shared processor is NOT touched
	// (it lives for the parent's lifetime). The context is short
	// (5s) — this only flushes pending records, no I/O.
	shutdown := func() error {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return childProvider.Shutdown(ctx)
	}
	return slog.New(handler), shutdown
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
	if existing := m.getProcess("llama"); existing != nil {
		existing.mu.RLock()
		if existing.state == StateRunning {
			existing.mu.RUnlock()
			return fmt.Errorf("llama-server already running (PID %d)", existing.pid)
		}
		existing.mu.RUnlock()
	}

	// Build new proc with all initialization under proc.mu. The
	// logWriter is a per-child OTel-aware childWriter (see
	// child_writer.go) — the manager already knows this is the
	// llama process, so the writer's service.name resource
	// attribute is set to "llama-server" at construction; no
	// per-line regex is needed.
	childLogger, childLogShutdown := m.childLoggerFor("llama")
	proc := &managedProcess{
		name:             "llama",
		state:            StateStarting,
		logWriter:        newChildWriter(childLogger),
		childLogProvider: childLogShutdown,
		done:             make(chan struct{}),
	}

	// Pre-populate state that we want to preserve from a prior run, under proc.mu.
	if existing := m.getProcess("llama"); existing != nil {
		existing.mu.RLock()
		proc.restartCount = existing.restartCount
		proc.logWriter = existing.logWriter
		existing.mu.RUnlock()
	}

	// Insert into map under m.mu.
	m.mu.Lock()
	m.processes["llama"] = proc
	m.mu.Unlock()

	// Set up the exec.Cmd outside any lock (exec.Command is safe to call).
	args := m.cfg.BuildLlamaArgs()
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

	return nil
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
	time.Sleep(time.Second)
	return m.StartLlama()
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

	// Check existing proc under proc.mu.
	if existing := m.getProcess(name); existing != nil {
		existing.mu.RLock()
		if existing.state == StateRunning {
			existing.mu.RUnlock()
			return fmt.Errorf("%s already running (PID %d)", name, existing.pid)
		}
		existing.mu.RUnlock()
	}

	// Build new proc with initial state under proc.mu. The
	// logWriter is a per-child childWriter (see child_writer.go)
	// with service.name set to the child's component label
	// (e.g., "node-exporter", "nvidia-exporter") at construction;
	// no per-line regex is needed.
	childLogger, childLogShutdown := m.childLoggerFor(name)
	proc := &managedProcess{
		name:             name,
		state:            StateStarting,
		logWriter:        newChildWriter(childLogger),
		childLogProvider: childLogShutdown,
		done:             make(chan struct{}),
	}

	// Preserve restart count if a prior instance exists.
	if existing := m.getProcess(name); existing != nil {
		existing.mu.RLock()
		proc.restartCount = existing.restartCount
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
	if proc.state != StateRunning {
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

	// Shutdown the child OTel LoggerProvider (flushes the
	// in-process batch, releases the queue). The shared
	// processor is NOT touched — it lives for the parent's
	// lifetime. This is critical to prevent the leak flagged
	// in PR #369 review: every StartLlama/StartService call
	// builds a fresh provider, and without this shutdown a
	// crash-looping child accumulates providers until OOM.
	proc.mu.Lock()
	shutdownFn := proc.childLogProvider
	proc.state = StateStopped
	proc.pid = 0
	proc.mu.Unlock()
	if shutdownFn != nil {
		if err := shutdownFn(); err != nil {
			m.logger.Warn("child OTel LoggerProvider shutdown failed",
				"name", name, "error", err)
		}
	}

	// Flush the child writer's remaining buffered bytes (e.g.,
	// a final log line without a trailing newline on a crash).
	// This is the trailing-partial-line fix from PR #369 review.
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
		RestartCount: proc.restartCount,
		LastExitCode: proc.lastExitCode,
		LastError:    proc.lastError,
	}

	if proc.state == StateRunning && !proc.startedAt.IsZero() {
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
