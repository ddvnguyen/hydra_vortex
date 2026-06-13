package health

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"
	"time"
)

func TestCheckerIdleMode(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/slots" {
			slots := []SlotStatus{
				{ID: 0, IsProcessing: false, NPast: 0, NRemain: 0},
				{ID: 1, IsProcessing: false, NPast: 0, NRemain: 0},
			}
			json.NewEncoder(w).Encode(slots)
		}
	}))
	defer server.Close()

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	checker := NewChecker(server.URL, logger, 100*time.Millisecond, 500*time.Millisecond, 3)
	defer checker.Stop()

	checker.Start()
	time.Sleep(200 * time.Millisecond)

	if checker.GetMode() != ModeIdle {
		t.Errorf("expected mode=idle, got %s", checker.GetMode())
	}
}

func TestCheckerBusyMode(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/slots" {
			slots := []SlotStatus{
				{ID: 0, IsProcessing: true, NPast: 100, NRemain: 50},
			}
			json.NewEncoder(w).Encode(slots)
		}
	}))
	defer server.Close()

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	checker := NewChecker(server.URL, logger, 100*time.Millisecond, 500*time.Millisecond, 3)
	defer checker.Stop()

	checker.Start()
	time.Sleep(200 * time.Millisecond)

	if checker.GetMode() != ModeBusy {
		t.Errorf("expected mode=busy, got %s", checker.GetMode())
	}
}

func TestCheckerUnhealthy(t *testing.T) {
	callCount := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		callCount++
		if callCount <= 3 {
			w.WriteHeader(http.StatusInternalServerError)
		} else {
			slots := []SlotStatus{{ID: 0, IsProcessing: false}}
			json.NewEncoder(w).Encode(slots)
		}
	}))
	defer server.Close()

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	checker := NewChecker(server.URL, logger, 50*time.Millisecond, 100*time.Millisecond, 3)
	defer checker.Stop()

	restartCalled := false
	checker.SetOnUnhealthy(func() {
		restartCalled = true
	})

	checker.Start()
	time.Sleep(300 * time.Millisecond)

	if !restartCalled {
		t.Error("expected onUnhealthy to be called")
	}
}

func TestCheckerHealthy(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		slots := []SlotStatus{{ID: 0, IsProcessing: false}}
		json.NewEncoder(w).Encode(slots)
	}))
	defer server.Close()

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	checker := NewChecker(server.URL, logger, 50*time.Millisecond, 100*time.Millisecond, 3)
	defer checker.Stop()

	checker.Start()
	time.Sleep(150 * time.Millisecond)

	if !checker.IsHealthy() {
		t.Error("expected checker to be healthy")
	}
}
