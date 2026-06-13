package api

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/config"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/health"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/process"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/registry"
)

func setupTestServer(t *testing.T) (*Server, func()) {
	tmpDir := t.TempDir()

	mockBinary := filepath.Join(tmpDir, "mock-server")
	script := `#!/bin/bash
echo "mock server"
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
	manager := process.NewManager(cfg, logger)
	checker := health.NewChecker("http://localhost:18080", logger, 10*time.Second, 30*time.Second, 3)
	regMgr := registry.NewManager(logger, tmpDir)

	server := NewServer(cfg, manager, checker, regMgr, logger)

	cleanup := func() {
		manager.Shutdown()
		checker.Stop()
	}

	return server, cleanup
}

func TestStatusEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t)
	defer cleanup()

	req := httptest.NewRequest(http.MethodGet, "/status", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected status 200, got %d", w.Code)
	}

	var response map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if _, ok := response["processes"]; !ok {
		t.Error("expected processes in response")
	}
	if _, ok := response["health"]; !ok {
		t.Error("expected health in response")
	}
}

func TestConfigEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t)
	defer cleanup()

	req := httptest.NewRequest(http.MethodGet, "/config", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected status 200, got %d", w.Code)
	}

	var cfg config.Config
	if err := json.Unmarshal(w.Body.Bytes(), &cfg); err != nil {
		t.Fatal(err)
	}

	if cfg.Node.Name != "test" {
		t.Errorf("expected node.name=test, got %s", cfg.Node.Name)
	}
}

func TestHealthEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t)
	defer cleanup()

	req := httptest.NewRequest(http.MethodGet, "/health", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected status 200, got %d", w.Code)
	}

	var response map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if response["status"] != "ok" {
		t.Errorf("expected status=ok, got %v", response["status"])
	}
	if response["node"] != "test" {
		t.Errorf("expected node=test, got %v", response["node"])
	}
}

func TestRestartEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t)
	defer cleanup()

	req := httptest.NewRequest(http.MethodPost, "/restart", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected status 200, got %d", w.Code)
	}

	var response map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if response["status"] != "restarted" {
		t.Errorf("expected status=restarted, got %s", response["status"])
	}
}
