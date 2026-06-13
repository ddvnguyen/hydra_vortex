package api

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"strings"

	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/config"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/health"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/process"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/registry"
)

type Server struct {
	cfg       *config.Config
	manager   *process.Manager
	checker   *health.Checker
	registry  *registry.Manager
	logger    *slog.Logger
	mux       *http.ServeMux
	authToken string // shared secret for API authentication
}

func NewServer(cfg *config.Config, manager *process.Manager, checker *health.Checker, regMgr *registry.Manager, logger *slog.Logger, authToken string) *Server {
	s := &Server{
		cfg:       cfg,
		manager:   manager,
		checker:   checker,
		registry:  regMgr,
		logger:    logger,
		mux:       http.NewServeMux(),
		authToken: authToken,
	}

	// Read-only endpoints (no auth required)
	s.mux.HandleFunc("/status", s.handleStatus)
	s.mux.HandleFunc("/health", s.handleHealth)
	s.mux.HandleFunc("/config", s.handleConfig)

	// Write endpoints (auth required)
	s.mux.HandleFunc("/restart", s.requireAuth(s.handleRestart))
	s.mux.HandleFunc("/update", s.requireAuth(s.handleUpdate))

	return s
}

// requireAuth wraps a handler with token authentication
func (s *Server) requireAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if s.authToken == "" {
			// No token configured - allow access (development mode)
			next(w, r)
			return
		}

		// Check Authorization header
		auth := r.Header.Get("Authorization")
		if auth == "" {
			http.Error(w, "authorization required", http.StatusUnauthorized)
			return
		}

		// Expect "Bearer <token>"
		if !strings.HasPrefix(auth, "Bearer ") {
			http.Error(w, "invalid authorization format", http.StatusUnauthorized)
			return
		}

		token := strings.TrimPrefix(auth, "Bearer ")
		if token != s.authToken {
			http.Error(w, "invalid token", http.StatusUnauthorized)
			return
		}

		next(w, r)
	}
}

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	s.mux.ServeHTTP(w, r)
}

func (s *Server) handleStatus(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	processes := s.manager.GetAllProcessInfo()

	response := struct {
		Processes map[string]*process.ProcessInfo `json:"processes"`
		Health    struct {
			Mode    string `json:"mode"`
			Healthy bool   `json:"healthy"`
		} `json:"health"`
	}{
		Processes: processes,
	}

	if s.checker != nil {
		response.Health.Mode = string(s.checker.GetMode())
		response.Health.Healthy = s.checker.IsHealthy()
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}

func (s *Server) handleConfig(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(s.cfg)

	case http.MethodPut:
		http.Error(w, "config update not implemented yet", http.StatusNotImplemented)

	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (s *Server) handleRestart(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	name := r.URL.Query().Get("name")
	if name == "" || name == "llama" {
		if err := s.manager.RestartLlama(); err != nil {
			s.logger.Error("restart failed", "name", "llama", "error", err)
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]string{"status": "restarted", "name": "llama"})
		return
	}

	if err := s.manager.RestartService(name); err != nil {
		s.logger.Error("restart failed", "name", name, "error", err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"status": "restarted", "name": name})
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	response := struct {
		Status  string `json:"status"`
		Node    string `json:"node"`
		Healthy bool   `json:"healthy"`
	}{
		Status:  "ok",
		Node:    s.cfg.Node.Name,
		Healthy: true,
	}

	if s.checker != nil {
		response.Healthy = s.checker.IsHealthy()
		if !response.Healthy {
			response.Status = "degraded"
		}
	}

	w.Header().Set("Content-Type", "application/json")
	if !response.Healthy {
		w.WriteHeader(http.StatusServiceUnavailable)
	}
	json.NewEncoder(w).Encode(response)
}

func (s *Server) handleUpdate(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	if s.registry == nil {
		http.Error(w, "registry manager not configured", http.StatusInternalServerError)
		return
	}

	var req struct {
		Name     string `json:"name"`
		Source   string `json:"source"`
		Checksum string `json:"checksum"`
		Binary   string `json:"binary"`
		Dest     string `json:"dest"`
	}

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	if req.Name == "" || req.Source == "" {
		http.Error(w, "name and source are required", http.StatusBadRequest)
		return
	}

	// Get binary config from config or use request values
	spec, exists := s.cfg.Binaries[req.Name]
	if !exists {
		// Create a new spec from request
		spec = config.BinaryConfig{
			Source:   req.Source,
			Checksum: req.Checksum,
			Binary:   req.Binary,
			Dest:     req.Dest,
		}
	} else {
		// Override with request values if provided
		if req.Source != "" {
			spec.Source = req.Source
		}
		if req.Checksum != "" {
			spec.Checksum = req.Checksum
		}
		if req.Binary != "" {
			spec.Binary = req.Binary
		}
		if req.Dest != "" {
			spec.Dest = req.Dest
		}
	}

	dest := spec.Dest
	if dest == "" {
		dest = "/opt/hydra/bin/" + req.Name
	}

	// Validate destination path - must be under allowed directories
	if !s.isPathAllowed(dest) {
		http.Error(w, "destination path not allowed", http.StatusBadRequest)
		return
	}

	binaryName := spec.Binary
	if binaryName == "" {
		binaryName = req.Name
	}

	s.logger.Info("updating binary", "name", req.Name, "source", spec.Source)

	if err := s.registry.PullBinary(spec.Source, dest, spec.Checksum, binaryName); err != nil {
		s.logger.Error("failed to update binary", "name", req.Name, "error", err)
		http.Error(w, "failed to update binary: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{
		"status":  "updated",
		"name":    req.Name,
		"dest":    dest,
		"message": "Binary updated successfully. Restart llama-server to use the new version.",
	})
}

// isPathAllowed checks if the destination path is within allowed directories
func (s *Server) isPathAllowed(path string) bool {
	// Allowlist of allowed directories for binary updates
	allowedDirs := []string{
		"/opt/hydra/bin",
		"/usr/local/bin",
		"/home/hydra/bin",
	}

	for _, dir := range allowedDirs {
		if strings.HasPrefix(path, dir+"/") || path == dir {
			return true
		}
	}
	return false
}
