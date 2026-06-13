package process

import (
	"log/slog"
	"os"
	"path/filepath"
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
	mgr := NewManager(cfg, logger)
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
	mgr := NewManager(cfg, logger)
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
	mgr := NewManager(cfg, logger)
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
