package process

import (
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/config"
)

func TestManagerStartStop(t *testing.T) {
	tmpDir := t.TempDir()

	mockBinary := filepath.Join(tmpDir, "mock-server")
	script := `#!/bin/bash
echo "mock server started"
sleep 10
`
	if err := os.WriteFile(mockBinary, []byte(script), 0755); err != nil {
		t.Fatal(err)
	}

	cfg := &config.Config{
		Node: config.NodeConfig{Name: "test"},
		Llama: config.LlamaConfig{
			Binary:     mockBinary,
			WorkingDir: tmpDir,
			Host:       "127.0.0.1",
			Port:       18080,
			RPCPort:    19503,
			Params:     map[string]any{},
			Env:        map[string]string{},
		},
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	time.Sleep(100 * time.Millisecond)

	info, err := mgr.GetProcessInfo("llama")
	if err != nil {
		t.Fatal(err)
	}

	if info.State != StateRunning {
		t.Errorf("expected state=running, got %s", info.State)
	}
	if info.PID == 0 {
		t.Error("expected non-zero PID")
	}

	if err := mgr.StopLlama(); err != nil {
		t.Fatal(err)
	}

	time.Sleep(100 * time.Millisecond)

	info, err = mgr.GetProcessInfo("llama")
	if err != nil {
		t.Fatal(err)
	}

	if info.State != StateStopped {
		t.Errorf("expected state=stopped, got %s", info.State)
	}
}

func TestManagerRestart(t *testing.T) {
	tmpDir := t.TempDir()

	mockBinary := filepath.Join(tmpDir, "mock-server")
	script := `#!/bin/bash
echo "mock server started"
sleep 10
`
	if err := os.WriteFile(mockBinary, []byte(script), 0755); err != nil {
		t.Fatal(err)
	}

	cfg := &config.Config{
		Node: config.NodeConfig{Name: "test"},
		Llama: config.LlamaConfig{
			Binary:     mockBinary,
			WorkingDir: tmpDir,
			Host:       "127.0.0.1",
			Port:       18081,
			RPCPort:    19504,
			Params:     map[string]any{},
			Env:        map[string]string{},
		},
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	time.Sleep(100 * time.Millisecond)

	info1, _ := mgr.GetProcessInfo("llama")
	pid1 := info1.PID

	if err := mgr.RestartLlama(); err != nil {
		t.Fatal(err)
	}

	time.Sleep(200 * time.Millisecond)

	info2, _ := mgr.GetProcessInfo("llama")
	pid2 := info2.PID

	if pid1 == pid2 {
		t.Error("expected different PID after restart")
	}
	if info2.State != StateRunning {
		t.Errorf("expected state=running after restart, got %s", info2.State)
	}
}

func TestManagerAutoRestart(t *testing.T) {
	tmpDir := t.TempDir()

	mockBinary := filepath.Join(tmpDir, "mock-server")
	script := `#!/bin/bash
echo "mock server started"
exit 1
`
	if err := os.WriteFile(mockBinary, []byte(script), 0755); err != nil {
		t.Fatal(err)
	}

	cfg := &config.Config{
		Node: config.NodeConfig{Name: "test"},
		Llama: config.LlamaConfig{
			Binary:     mockBinary,
			WorkingDir: tmpDir,
			Host:       "127.0.0.1",
			Port:       18082,
			RPCPort:    19505,
			Params:     map[string]any{},
			Env:        map[string]string{},
		},
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	time.Sleep(3 * time.Second)

	info, _ := mgr.GetProcessInfo("llama")
	if info.RestartCount == 0 {
		t.Error("expected auto-restart to have occurred")
	}
}

// TestConcurrentStartStopStateAccess runs a herd of goroutines that
// hammer Start/Stop/Restart/GetAllProcessInfo against a manager that
// has both a "llama" and a sub-service registered. The point is to
// expose any residual data race on the per-process state fields
// (state, manualStop, pid, lastError, etc.) that would only be
// visible under the race detector.
//
// Run with: go test -race ./internal/process/...
func TestConcurrentStartStopStateAccess(t *testing.T) {
	// Use /bin/sleep as the mock binary — it's a single C program that
	// handles SIGTERM cleanly (exits immediately) and lets Kill work too.
	const mockBinary = "/bin/sleep"

	cfg := &config.Config{
		Node: config.NodeConfig{Name: "test"},
		Llama: config.LlamaConfig{
			Binary:     mockBinary,
			WorkingDir: t.TempDir(),
			Host:       "127.0.0.1",
			Port:       18090,
			RPCPort:    19510,
			Params:     map[string]any{},
			Env:        map[string]string{},
		},
		Services: config.ServicesConfig{
			Promtail: config.ServiceConfig{
				Enabled: true,
				Binary:  mockBinary,
				Config:  "/dev/null",
			},
		},
	}

	logger := slog.New(slog.NewTextHandler(os.Stderr, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	const testDuration = 2 * time.Second
	stop := make(chan struct{})
	var wg sync.WaitGroup
	var startCount, stopCount, infoCount atomic.Int64

	// Goroutine A: rapid start/stop on llama.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
				_ = mgr.StartLlama()
				startCount.Add(1)
				_ = mgr.StopLlama()
				stopCount.Add(1)
			}
		}
	}()

	// Goroutine B: rapid start/stop on the promtail service.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
				_ = mgr.StartService("promtail")
				startCount.Add(1)
				_ = mgr.StopService("promtail")
				stopCount.Add(1)
			}
		}
	}()

	// Goroutine C: poll GetAllProcessInfo / GetProcessInfo continuously.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
				_ = mgr.GetAllProcessInfo()
				_, _ = mgr.GetProcessInfo("llama")
				_, _ = mgr.GetProcessInfo("promtail")
				infoCount.Add(1)
			}
		}
	}()

	// Goroutine D: trigger restart storms.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
				_ = mgr.RestartLlama()
				_ = mgr.RestartService("promtail")
			}
		}
	}()

	time.Sleep(testDuration)
	close(stop)
	wg.Wait()

	t.Logf("concurrent ops: starts=%d stops=%d info=%d",
		startCount.Load(), stopCount.Load(), infoCount.Load())
}

