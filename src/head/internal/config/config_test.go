package config

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadAndMerge(t *testing.T) {
	tmpDir := t.TempDir()

	globalYAML := `
node:
  name: default
  mode: vm

llama:
  binary: /usr/local/bin/llama-server
  host: 0.0.0.0
  port: 8080
  rpc_port: 9503
  params:
    flash-attn: on
    cache-type-k: q8_0
    ctx-size: 32768

infra:
  prometheus:
    host: 10.0.0.1
    port: 9091
  loki:
    url: http://10.0.0.1:3100

services:
  promtail:
    enabled: true
    binary: /usr/local/bin/promtail
  node_exporter:
    enabled: true
    binary: /usr/local/bin/node_exporter
    port: 9100
`

	nodeYAML := `
node:
  name: p100
  mode: vm

llama:
  binary: /opt/hydra/bin/llama-server
  port: 8086
  rpc_port: 9502
  params:
    ctx-size: 180000
    parallel: 1
  env:
    CUDA_VISIBLE_DEVICES: "0"

services:
  nvidia_exporter:
    enabled: true
    binary: /opt/hydra/bin/nvidia-exporter
    port: 9400
`

	globalPath := filepath.Join(tmpDir, "global.yaml")
	nodePath := filepath.Join(tmpDir, "node.yaml")

	if err := os.WriteFile(globalPath, []byte(globalYAML), 0644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(nodePath, []byte(nodeYAML), 0644); err != nil {
		t.Fatal(err)
	}

	cfg, err := Load(globalPath, nodePath)
	if err != nil {
		t.Fatal(err)
	}

	if cfg.Node.Name != "p100" {
		t.Errorf("expected node.name=p100, got %s", cfg.Node.Name)
	}
	if cfg.Node.Mode != "vm" {
		t.Errorf("expected node.mode=vm, got %s", cfg.Node.Mode)
	}

	if cfg.Llama.Binary != "/opt/hydra/bin/llama-server" {
		t.Errorf("expected node binary, got %s", cfg.Llama.Binary)
	}
	if cfg.Llama.Host != "0.0.0.0" {
		t.Errorf("expected host from global, got %s", cfg.Llama.Host)
	}
	if cfg.Llama.Port != 8086 {
		t.Errorf("expected node port, got %d", cfg.Llama.Port)
	}
	if cfg.Llama.RPCPort != 9502 {
		t.Errorf("expected node rpc_port, got %d", cfg.Llama.RPCPort)
	}

	if cfg.Llama.Params["ctx-size"] != 180000 {
		t.Errorf("expected node ctx-size, got %v", cfg.Llama.Params["ctx-size"])
	}
	if cfg.Llama.Params["flash-attn"] != "on" {
		t.Errorf("expected global flash-attn, got %v", cfg.Llama.Params["flash-attn"])
	}
	if cfg.Llama.Params["cache-type-k"] != "q8_0" {
		t.Errorf("expected global cache-type-k, got %v", cfg.Llama.Params["cache-type-k"])
	}

	if cfg.Llama.Env["CUDA_VISIBLE_DEVICES"] != "0" {
		t.Errorf("expected node env, got %v", cfg.Llama.Env["CUDA_VISIBLE_DEVICES"])
	}

	if cfg.Infra.Prometheus.Host != "10.0.0.1" {
		t.Errorf("expected global prometheus host, got %s", cfg.Infra.Prometheus.Host)
	}
	if cfg.Infra.Loki.URL != "http://10.0.0.1:3100" {
		t.Errorf("expected global loki url, got %s", cfg.Infra.Loki.URL)
	}

	if !cfg.Services.Promtail.Enabled {
		t.Error("expected promtail enabled from global")
	}
	if !cfg.Services.NodeExporter.Enabled {
		t.Error("expected node_exporter enabled from global")
	}
	if !cfg.Services.NvidiaExporter.Enabled {
		t.Error("expected nvidia_exporter enabled from node")
	}
}

func TestBuildLlamaArgs(t *testing.T) {
	cfg := &Config{
		Llama: LlamaConfig{
			Host:    "0.0.0.0",
			Port:    8080,
			RPCPort: 9503,
			Params: map[string]any{
				"ctx-size":     32768,
				"flash-attn":   "on",
				"cache-type-k": "q8_0",
				"parallel":     2,
				"metrics":      true,
			},
		},
	}

	args := cfg.BuildLlamaArgs()

	expected := map[string]bool{
		"--host":         false,
		"--port":         false,
		"--rpc-port":     false,
		"--ctx-size":     false,
		"--flash-attn":   false,
		"--cache-type-k": false,
		"--parallel":     false,
		"--metrics":      false,
	}

	for _, arg := range args {
		if _, ok := expected[arg]; ok {
			expected[arg] = true
		}
	}

	for flag, found := range expected {
		if !found {
			t.Errorf("expected flag %s not found in args", flag)
		}
	}
}

// Hydra #383 T3: golden-args test for the 27B peer-only config.
func TestBuildLlamaArgs_PeerOnly27B(t *testing.T) {
	cfg := Config{
		Llama: LlamaConfig{
			Binary:     "/llama/bin/llama-engine",
			WorkingDir: "/llama",
			Host:       "0.0.0.0",
			Port:       8081,
			RPCPort:    0,
			Params: map[string]any{
				"peer-only":     true,
				"ggml-rpc-port": 9506,
			},
			Env: map[string]string{
				"NVIDIA_VISIBLE_DEVICES":    "1",
				"NVIDIA_DRIVER_CAPABILITIES": "compute,utility",
			},
		},
	}
	args := cfg.BuildLlamaArgs()

	expected := []string{
		"--host", "0.0.0.0",
		"--port", "8081",
		"--rpc-port", "0",
		"--ggml-rpc-port", "9506",
		"--peer-only",
	}
	if len(args) != len(expected) {
		t.Errorf("args length: got %d, want %d\n  got:  %v\n  want: %v",
			len(args), len(expected), args, expected)
	}
	for i := range expected {
		if i >= len(args) || args[i] != expected[i] {
			t.Errorf("args[%d]: got %q, want %q\n  full got:  %v\n  full want: %v",
				i, args[i], expected[i], args, expected)
		}
	}
}

// Hydra #383 T3: golden-args test for the full Dense profile.
// Verifies that BuildLlamaArgs produces the exact ordered CLI args
// from the node-rtx-27b.yaml config.
func TestBuildLlamaArgs_DenseProfile(t *testing.T) {
	cfg := &Config{
		Llama: LlamaConfig{
			Host:    "0.0.0.0",
			Port:    8080,
			RPCPort: 9503,
			Params: map[string]any{
				"cache-type-k":        "q8_0",
				"cache-type-v":        "q8_0",
				"combined-split-mode": "layer",
				"combined-tensor-split": "21/44",
				"cont-batching":       true,
				"ctx-size":            8192,
				"flash-attn":          "on",
				"metrics":             true,
				"model":               "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
				"n-gpu-layers":        66,
				"parallel":            1,
				"perf":                true,
				"rpc-engine":          "localhost:9506",
				"slots":               true,
				"ubatch-size":         384,
			},
		},
	}

	args := cfg.BuildLlamaArgs()

	expected := []string{
		"--host", "0.0.0.0",
		"--port", "8080",
		"--rpc-port", "9503",
		"--cache-type-k", "q8_0",
		"--cache-type-v", "q8_0",
		"--combined-split-mode", "layer",
		"--combined-tensor-split", "21/44",
		"--cont-batching",
		"--ctx-size", "8192",
		"--flash-attn", "on",
		"--metrics",
		"--model", "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
		"--n-gpu-layers", "66",
		"--parallel", "1",
		"--perf",
		"--rpc-engine", "localhost:9506",
		"--slots",
		"--ubatch-size", "384",
	}

	if len(args) != len(expected) {
		t.Errorf("args length: got %d, want %d\n  got:  %v\n  want: %v",
			len(args), len(expected), args, expected)
		return
	}
	for i := range expected {
		if args[i] != expected[i] {
			t.Errorf("args[%d]: got %q, want %q\n  full got:  %v\n  full want: %v",
				i, args[i], expected[i], args, expected)
		}
	}
}

func TestValidate(t *testing.T) {
	tests := []struct {
		name    string
		cfg     *Config
		wantErr bool
	}{
		{
			name: "valid config",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-server",
					Port:    8080,
					RPCPort: 9503,
				},
			},
			wantErr: false,
		},
		{
			name: "missing node name",
			cfg: &Config{
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-server",
					Port:    8080,
					RPCPort: 9503,
				},
			},
			wantErr: true,
		},
		{
			name: "missing binary",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Port:    8080,
					RPCPort: 9503,
				},
			},
			wantErr: true,
		},
		{
			name: "missing port",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-server",
					RPCPort: 9503,
				},
			},
			wantErr: true,
		},
		{
			name: "combined-split-mode=layer with combined-ot-pattern rejected",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-engine",
					Port:    8080,
					RPCPort: 9503,
					Params: map[string]any{
						"combined-split-mode":  "layer",
						"combined-ot-pattern":  "blk\\.([0-9]+)\\.ffn_.*_exps\\.weight=CPU",
						"combined-tensor-split": "21/44",
					},
				},
			},
			wantErr: true,
		},
		{
			name: "combined-split-mode=layer missing combined-tensor-split",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-engine",
					Port:    8080,
					RPCPort: 9503,
					Params: map[string]any{
						"combined-split-mode": "layer",
					},
				},
			},
			wantErr: true,
		},
		{
			name: "list param rejected",
			cfg: &Config{
				Node: NodeConfig{Name: "test"},
				Llama: LlamaConfig{
					Binary:  "/usr/bin/llama-engine",
					Port:    8080,
					RPCPort: 9503,
					Params: map[string]any{
						"override-tensor": []any{"a", "b"},
					},
				},
			},
			wantErr: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := tt.cfg.Validate()
			if (err != nil) != tt.wantErr {
				t.Errorf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestGeneratePromtailConfig(t *testing.T) {
	tmpDir := t.TempDir()
	outputPath := filepath.Join(tmpDir, "promtail.yml")

	cfg := &Config{
		Node: NodeConfig{Name: "p100"},
		Services: ServicesConfig{
			Promtail: ServiceConfig{Enabled: true},
		},
		Infra: InfraConfig{
			Loki: EndpointConfig{URL: "http://10.0.0.1:3100"},
		},
	}

	if err := cfg.GeneratePromtailConfig(outputPath); err != nil {
		t.Fatal(err)
	}

	data, err := os.ReadFile(outputPath)
	if err != nil {
		t.Fatal(err)
	}

	content := string(data)
	if !contains(content, "http://10.0.0.1:3100/loki/api/v1/push") {
		t.Error("expected loki URL in promtail config")
	}
	if !contains(content, "node: p100") {
		t.Error("expected node name in promtail config")
	}
}

func contains(s, substr string) bool {
	return len(s) >= len(substr) && (s == substr || len(s) > len(substr) && (s[:len(substr)] == substr || contains(s[1:], substr)))
}
