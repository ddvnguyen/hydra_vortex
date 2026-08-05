package process

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
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

// readinessTestCfg builds a manager config with a mock binary whose stdout
// emits the given lines (via echo), plus a readiness sentinel contract.
func readinessTestCfg(t *testing.T, stdoutLines []string, sentinels []string, timeoutSec int) (*config.Config, string) {
	t.Helper()
	tmpDir := t.TempDir()
	mockBinary := filepath.Join(tmpDir, "mock-server")
	var b strings.Builder
	b.WriteString("#!/bin/bash\n")
	for _, line := range stdoutLines {
		fmt.Fprintf(&b, "echo %q\n", line)
	}
	b.WriteString("sleep 10\n")
	if err := os.WriteFile(mockBinary, []byte(b.String()), 0755); err != nil {
		t.Fatal(err)
	}
	cfg := &config.Config{
		Node: config.NodeConfig{Name: "test"},
		Llama: config.LlamaConfig{
			Binary:     mockBinary,
			WorkingDir: tmpDir,
			Host:       "127.0.0.1",
			Port:       18083,
			RPCPort:    19506,
			Params:     map[string]any{},
			Env:        map[string]string{},
		},
		Readiness: config.ReadinessConfig{
			Sentinels:  sentinels,
			TimeoutSec: timeoutSec,
		},
	}
	return cfg, mockBinary
}

// TestReadinessSentinelMarksReady: a stdout line containing the sentinel
// transitions the process to READY with no HTTP probe.
func TestReadinessSentinelMarksReady(t *testing.T) {
	cfg, _ := readinessTestCfg(t,
		[]string{"some boot log line", "router server is listening on http://0.0.0.0:8080"},
		[]string{"router server is listening on"}, 60)

	logger := slog.New(slog.NewTextHandler(os.Stderr, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		info, err := mgr.GetProcessInfo("llama")
		if err == nil && info.Ready {
			if info.State != StateReady {
				t.Errorf("state = %s, want %s", info.State, StateReady)
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("llama never became ready from stdout sentinel")
}

// TestReadinessNoSentinelAssumeReady: without a readiness contract the
// process is ready once alive (legacy behaviour).
func TestReadinessNoSentinelAssumeReady(t *testing.T) {
	cfg, _ := readinessTestCfg(t,
		[]string{"mock server started"},
		nil, 60)

	logger := slog.New(slog.NewTextHandler(os.Stderr, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	info, err := mgr.GetProcessInfo("llama")
	if err != nil {
		t.Fatal(err)
	}
	if !info.Ready {
		t.Error("expected ready=true without a readiness contract")
	}
}

// TestReadinessTimeoutMarksSuspect: no sentinel within the timeout marks the
// process SUSPECT (started, not ready), but does not restart it (a slow model
// load is legitimate; crashes are handled by the exit event).
func TestReadinessTimeoutMarksSuspect(t *testing.T) {
	cfg, _ := readinessTestCfg(t,
		[]string{"silently loading a big model..."}, // never contains the sentinel
		[]string{"server is listening on"}, 1)        // 1s miss-deadline

	logger := slog.New(slog.NewTextHandler(os.Stderr, nil))
	mgr := NewManager(cfg, logger, nil)
	defer mgr.Shutdown()

	if err := mgr.StartLlama(); err != nil {
		t.Fatal(err)
	}

	deadline := time.Now().Add(4 * time.Second)
	for time.Now().Before(deadline) {
		info, err := mgr.GetProcessInfo("llama")
		if err == nil && info.State == StateSuspect {
			if info.Ready {
				t.Error("expected ready=false in suspect state")
			}
			// The process must still be alive (no restart from the timeout).
			if _, err := os.Stat("/proc/" + fmt.Sprintf("%d", info.PID)); err != nil {
				t.Errorf("process should still be alive in suspect state: %v", err)
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("llama never became suspect after readiness timeout")
}

