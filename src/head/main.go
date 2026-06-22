package main

import (
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/api"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/config"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/health"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/process"
	"github.com/ddvnguyen/hydra_vortex/hydra-head/internal/registry"
)

func main() {
	globalConfig := flag.String("global", "", "Path to global config file")
	nodeConfig := flag.String("node", "", "Path to node config file")
	apiPort := flag.Int("api-port", 9700, "API server port")
	authToken := flag.String("auth-token", "", "Authentication token for API (or set HYDRA_HEAD_AUTH_TOKEN)")
	flag.Parse()

	if *globalConfig == "" || *nodeConfig == "" {
		fmt.Fprintf(os.Stderr, "Error: both -global and -node config files are required\n")
		flag.Usage()
		os.Exit(1)
	}

	// Fall back to environment variable if flag not provided
	if *authToken == "" {
		*authToken = os.Getenv("HYDRA_HEAD_AUTH_TOKEN")
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	}))

	logger.Info("hydra-head starting",
		"global_config", *globalConfig,
		"node_config", *nodeConfig,
		"api_port", *apiPort)

	cfg, err := config.Load(*globalConfig, *nodeConfig)
	if err != nil {
		logger.Error("failed to load config", "error", err)
		os.Exit(1)
	}

	if err := cfg.Validate(); err != nil {
		logger.Error("invalid config", "error", err)
		os.Exit(1)
	}

	logger.Info("config loaded",
		"node", cfg.Node.Name,
		"mode", cfg.Node.Mode,
		"llama_binary", cfg.Llama.Binary,
		"llama_port", cfg.Llama.Port)

	// Log the full merged config params for verification
	cfg.LogLlamaConfig(logger)

	// Pull binaries from OCI registry if configured
	var regMgr *registry.Manager
	if len(cfg.Binaries) > 0 {
		logger.Info("pulling binaries from OCI registry", "count", len(cfg.Binaries))
		regMgr = registry.NewManager(logger, "/tmp/hydra-registry-cache")

		for name, spec := range cfg.Binaries {
			if spec.Source == "" {
				continue
			}

			logger.Info("pulling binary", "name", name, "source", spec.Source)

			dest := spec.Dest
			if dest == "" {
				dest = fmt.Sprintf("/opt/hydra/bin/%s", name)
			}

			binaryName := spec.Binary
			if binaryName == "" {
				binaryName = name
			}

			// Skip pull if the binary is already on disk (e.g., baked into
			// the image at build time). This is the preferred path for
			// small sidecar exporters — it avoids a network round-trip on
			// every container start.
			if _, err := os.Stat(dest); err == nil {
				logger.Info("binary already present on disk, skipping pull",
					"name", name, "dest", dest)
				continue
			} else if !os.IsNotExist(err) {
				logger.Error("stat binary path", "name", name, "dest", dest, "error", err)
				os.Exit(1)
			}

			if err := regMgr.PullBinary(spec.Source, dest, spec.ImageDigest, spec.BinaryChecksum, binaryName); err != nil {
				logger.Error("failed to pull binary", "name", name, "error", err)
				os.Exit(1)
			}

			logger.Info("binary pulled successfully", "name", name, "dest", dest)
		}
	} else {
		// Create registry manager even if no binaries configured (for /update endpoint)
		regMgr = registry.NewManager(logger, "/tmp/hydra-registry-cache")
	}

	manager := process.NewManager(cfg, logger)
	defer manager.Shutdown()

	llamaURL := fmt.Sprintf("http://%s:%d", cfg.Llama.Host, cfg.Llama.Port)
	checker := health.NewChecker(
		llamaURL,
		cfg.Health.Path,
		logger,
		time.Duration(cfg.Health.IntervalIdleSec)*time.Second,
		time.Duration(cfg.Health.IntervalBusySec)*time.Second,
		cfg.Health.MaxFails,
	)
	checker.SetOnUnhealthy(func() {
		logger.Warn("llama-server unhealthy, triggering restart")
		if err := manager.RestartLlama(); err != nil {
			logger.Error("restart failed", "error", err)
		}
	})
	checker.Start()
	defer checker.Stop()

	if err := manager.StartLlama(); err != nil {
		logger.Error("failed to start llama-server", "error", err)
		os.Exit(1)
	}

	for _, name := range cfg.AllServiceNames() {
		svc := cfg.ServiceConfig(name)
		if !svc.Enabled {
			logger.Info("service disabled, skipping", "name", name)
			continue
		}
		if err := manager.StartService(name); err != nil {
			logger.Error("failed to start service", "name", name, "error", err)
		} else {
			logger.Info("service started", "name", name)
		}
	}

	apiServer := api.NewServer(cfg, manager, checker, regMgr, logger, *authToken)

	httpServer := &http.Server{
		Addr:    fmt.Sprintf(":%d", *apiPort),
		Handler: apiServer,
	}

	go func() {
		logger.Info("API server starting", "port", *apiPort)
		if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			logger.Error("API server failed", "error", err)
		}
	}()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	sig := <-sigCh
	logger.Info("received signal, shutting down", "signal", sig)

	if err := httpServer.Close(); err != nil {
		logger.Error("failed to close HTTP server", "error", err)
	}

	logger.Info("hydra-head stopped")
}
