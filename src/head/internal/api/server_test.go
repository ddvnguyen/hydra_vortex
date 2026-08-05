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
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/process"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/registry"
)

func setupTestServer(t *testing.T, authToken string) (*Server, func()) {
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
		Readiness: config.ReadinessConfig{
			Sentinels:  []string{"mock server"},
			TimeoutSec: 60,
		},
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	manager := process.NewManager(cfg, logger, nil)
	regMgr := registry.NewManager(logger, tmpDir)

	server := NewServer(cfg, manager, regMgr, logger, authToken)

	cleanup := func() {
		manager.Shutdown()
	}

	return server, cleanup
}

// startLlamaAndWaitReady starts the llama process and blocks until it is
// marked READY (the mock prints its sentinel on stdout immediately).
func startLlamaAndWaitReady(t *testing.T, server *Server) {
	t.Helper()
	if err := server.manager.StartLlama(); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if info, err := server.manager.GetProcessInfo("llama"); err == nil && info.Ready {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("llama did not become ready within 3s")
}

func TestStatusEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
	defer cleanup()

	req := httptest.NewRequest(http.MethodGet, "/status", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusServiceUnavailable {
		t.Errorf("expected 503 before llama ready, got %d", w.Code)
	}

	startLlamaAndWaitReady(t, server)

	req = httptest.NewRequest(http.MethodGet, "/status", nil)
	w = httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected status 200 after ready, got %d", w.Code)
	}

	var response map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if _, ok := response["processes"]; !ok {
		t.Error("expected processes in response")
	}
	healthResp, ok := response["health"].(map[string]any)
	if !ok {
		t.Fatal("expected health in response")
	}
	if healthResp["ready"] != true {
		t.Errorf("expected health.ready=true, got %v", healthResp["ready"])
	}
}

func TestConfigEndpoint(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
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
	server, cleanup := setupTestServer(t, "test-token")
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

func TestRestartEndpointRequiresAuth(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
	defer cleanup()

	// Test without auth token
	req := httptest.NewRequest(http.MethodPost, "/restart", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected status 401, got %d", w.Code)
	}
}

func TestRestartEndpointWithAuth(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
	defer cleanup()

	// Test with valid auth token
	req := httptest.NewRequest(http.MethodPost, "/restart", nil)
	req.Header.Set("Authorization", "Bearer test-token")
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

func TestRestartEndpointWithInvalidAuth(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
	defer cleanup()

	// Test with invalid auth token
	req := httptest.NewRequest(http.MethodPost, "/restart", nil)
	req.Header.Set("Authorization", "Bearer wrong-token")
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected status 401, got %d", w.Code)
	}
}

func TestAuthFailClosed(t *testing.T) {
	// Test that server with empty token denies access
	server, cleanup := setupTestServer(t, "")
	defer cleanup()

	req := httptest.NewRequest(http.MethodPost, "/restart", nil)
	w := httptest.NewRecorder()

	server.ServeHTTP(w, req)

	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected status 401 (fail-closed), got %d", w.Code)
	}
}

func TestPathValidation(t *testing.T) {
	server, cleanup := setupTestServer(t, "test-token")
	defer cleanup()

	tests := []struct {
		path     string
		expected bool
	}{
		{"/opt/hydra/bin/llama-server", true},
		{"/opt/hydra/bin/../etc/passwd", false},
		{"/usr/local/bin/test", true},
		{"/home/hydra/bin/test", true},
		{"/etc/passwd", false},
		{"/tmp/test", false},
		{"/opt/hydra/bin/../../etc/passwd", false},
	}

	for _, tt := range tests {
		result := server.isPathAllowed(tt.path)
		if result != tt.expected {
			t.Errorf("isPathAllowed(%q) = %v, expected %v", tt.path, result, tt.expected)
		}
	}
}
