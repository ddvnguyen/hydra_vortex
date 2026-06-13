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
)

type State string

const (
	StateStopped  State = "stopped"
	StateStarting State = "starting"
	StateRunning  State = "running"
	StateStopping State = "stopping"
)

type ProcessInfo struct {
	Name        string    `json:"name"`
	PID         int       `json:"pid"`
	State       State     `json:"state"`
	Uptime      string    `json:"uptime"`
	RestartCount int      `json:"restart_count"`
	LastExitCode int      `json:"last_exit_code"`
	LastError   string    `json:"last_error,omitempty"`
}

type Manager struct {
	cfg        *config.Config
	logger     *slog.Logger
	processes  map[string]*managedProcess
	mu         sync.RWMutex
	ctx        context.Context
	cancel     context.CancelFunc
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
	manualStop   bool
	startFunc    func() error
	mu           sync.RWMutex
}

func NewManager(cfg *config.Config, logger *slog.Logger) *Manager {
	ctx, cancel := context.WithCancel(context.Background())
	return &Manager{
		cfg:       cfg,
		logger:    logger,
		processes: make(map[string]*managedProcess),
		ctx:       ctx,
		cancel:    cancel,
	}
}

func (m *Manager) StartLlama() error {
	m.mu.Lock()
	defer m.mu.Unlock()

	if proc, exists := m.processes["llama"]; exists && proc.state == StateRunning {
		return fmt.Errorf("llama-server already running (PID %d)", proc.pid)
	}

	var restartCount int
	var logWriter io.Writer = os.Stdout
	if proc, exists := m.processes["llama"]; exists {
		restartCount = proc.restartCount
		logWriter = proc.logWriter
	}

	proc := &managedProcess{
		name:         "llama",
		state:        StateStarting,
		restartCount: restartCount,
		logWriter:    logWriter,
	}
	m.processes["llama"] = proc

	args := m.cfg.BuildLlamaArgs()
	cmd := exec.Command(m.cfg.Llama.Binary, args...)
	cmd.Dir = m.cfg.Llama.WorkingDir
	cmd.Stdout = proc.logWriter
	cmd.Stderr = proc.logWriter
	cmd.Env = os.Environ()
	for k, v := range m.cfg.Llama.Env {
		cmd.Env = append(cmd.Env, fmt.Sprintf("%s=%s", k, v))
	}

	proc.cmd = cmd
	proc.startFunc = func() error { return m.StartLlama() }

	if err := cmd.Start(); err != nil {
		proc.state = StateStopped
		proc.lastError = err.Error()
		return fmt.Errorf("start llama-server: %w", err)
	}

	proc.pid = cmd.Process.Pid
	proc.state = StateRunning
	proc.startedAt = time.Now()

	m.logger.Info("llama-server started", "pid", proc.pid, "args", args)

	go m.monitorProcess(proc)

	return nil
}

func (m *Manager) monitorProcess(proc *managedProcess) {
	err := proc.cmd.Wait()

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
	m.mu.Lock()
	proc, exists := m.processes["llama"]
	if !exists || proc.state != StateRunning {
		m.mu.Unlock()
		return fmt.Errorf("llama-server not running")
	}
	proc.state = StateStopping
	proc.manualStop = true
	m.mu.Unlock()

	m.logger.Info("stopping llama-server", "pid", proc.pid)

	if err := proc.cmd.Process.Signal(syscall.SIGTERM); err != nil {
		return fmt.Errorf("send SIGTERM: %w", err)
	}

	done := make(chan error, 1)
	go func() {
		done <- proc.cmd.Wait()
	}()

	select {
	case <-time.After(10 * time.Second):
		m.logger.Warn("llama-server did not exit gracefully, killing", "pid", proc.pid)
		if err := proc.cmd.Process.Kill(); err != nil {
			return fmt.Errorf("kill process: %w", err)
		}
		<-done
	case err := <-done:
		if err != nil {
			m.logger.Info("llama-server exited", "error", err)
		} else {
			m.logger.Info("llama-server exited cleanly")
		}
	}

	proc.mu.Lock()
	proc.state = StateStopped
	proc.pid = 0
	proc.mu.Unlock()

	return nil
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

	m.mu.Lock()
	if proc, exists := m.processes[name]; exists && proc.state == StateRunning {
		m.mu.Unlock()
		return fmt.Errorf("%s already running (PID %d)", name, proc.pid)
	}

	var restartCount int
	if proc, exists := m.processes[name]; exists {
		restartCount = proc.restartCount
	}

	proc := &managedProcess{
		name:         name,
		state:        StateStarting,
		restartCount: restartCount,
		logWriter:    os.Stdout,
	}
	m.processes[name] = proc
	m.mu.Unlock()

	binary := m.cfg.ServiceBinary(name)
	if binary == "" {
		return fmt.Errorf("no binary configured for %s", name)
	}

	args := m.cfg.BuildServiceArgs(name, svc)
	cmd := exec.Command(binary, args...)
	cmd.Stdout = proc.logWriter
	cmd.Stderr = proc.logWriter

	proc.cmd = cmd
	proc.startFunc = func() error { return m.StartService(name) }

	if err := cmd.Start(); err != nil {
		proc.mu.Lock()
		proc.state = StateStopped
		proc.lastError = err.Error()
		proc.mu.Unlock()
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
	m.mu.Lock()
	proc, exists := m.processes[name]
	if !exists || proc.state != StateRunning {
		m.mu.Unlock()
		return fmt.Errorf("%s not running", name)
	}
	proc.state = StateStopping
	proc.manualStop = true
	m.mu.Unlock()

	m.logger.Info("stopping service", "name", name, "pid", proc.pid)

	if err := proc.cmd.Process.Signal(syscall.SIGTERM); err != nil {
		return fmt.Errorf("send SIGTERM to %s: %w", name, err)
	}

	done := make(chan error, 1)
	go func() {
		done <- proc.cmd.Wait()
	}()

	select {
	case <-time.After(10 * time.Second):
		m.logger.Warn("service did not exit gracefully, killing", "name", name, "pid", proc.pid)
		if err := proc.cmd.Process.Kill(); err != nil {
			return fmt.Errorf("kill %s: %w", name, err)
		}
		<-done
	case err := <-done:
		if err != nil {
			m.logger.Info("service exited", "name", name, "error", err)
		} else {
			m.logger.Info("service exited cleanly", "name", name)
		}
	}

	proc.mu.Lock()
	proc.state = StateStopped
	proc.pid = 0
	proc.mu.Unlock()

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
	m.mu.RLock()
	proc, exists := m.processes[name]
	m.mu.RUnlock()

	if !exists {
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
	m.mu.RLock()
	defer m.mu.RUnlock()

	result := make(map[string]*ProcessInfo)
	for name := range m.processes {
		info, err := m.GetProcessInfo(name)
		if err == nil {
			result[name] = info
		}
	}
	return result
}

func (m *Manager) Shutdown() {
	m.logger.Info("shutting down process manager")
	m.cancel()

	m.mu.RLock()
	running := make([]string, 0)
	for name, proc := range m.processes {
		if proc.state == StateRunning {
			running = append(running, name)
		}
	}
	m.mu.RUnlock()

	for _, name := range running {
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
