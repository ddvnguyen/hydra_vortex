package config

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"time"

	"gopkg.in/yaml.v3"
)

type Config struct {
	Node     NodeConfig       `yaml:"node"`
	Llama    LlamaConfig      `yaml:"llama"`
	Services ServicesConfig   `yaml:"services"`
	Infra    InfraConfig      `yaml:"infra"`
	Binaries BinariesConfig   `yaml:"binaries"`
	Readiness ReadinessConfig `yaml:"readiness"`
}

type NodeConfig struct {
	Name string `yaml:"name"`
	Mode string `yaml:"mode"` // "container" or "vm"
}

type LlamaConfig struct {
	Binary     string            `yaml:"binary"`
	WorkingDir string            `yaml:"working_dir"`
	Host       string            `yaml:"host"`
	Port       int               `yaml:"port"`
	RPCPort    int               `yaml:"rpc_port"`
	Params     map[string]any    `yaml:"params"`
	Env        map[string]string `yaml:"env"`
}

type ServicesConfig struct {
	Promtail       ServiceConfig `yaml:"promtail"`
	NodeExporter   ServiceConfig `yaml:"node_exporter"`
	NvidiaExporter ServiceConfig `yaml:"nvidia_exporter"`
}

type ServiceConfig struct {
	Enabled bool     `yaml:"enabled"`
	Binary  string   `yaml:"binary"`
	Config  string   `yaml:"config"`
	Port    int      `yaml:"port"`
	Args    []string `yaml:"args"`
}

type InfraConfig struct {
	Prometheus EndpointConfig `yaml:"prometheus"`
	Loki       EndpointConfig `yaml:"loki"`
	Grafana    EndpointConfig `yaml:"grafana"`
	OTel       EndpointConfig `yaml:"otel"`
}

type EndpointConfig struct {
	Host string `yaml:"host"`
	Port int    `yaml:"port"`
	URL  string `yaml:"url"`
}

type BinariesConfig map[string]BinaryConfig

type BinaryConfig struct {
	Source         string `yaml:"source"`
	ImageDigest    string `yaml:"image_digest"`    // OCI image manifest digest (sha256:...)
	BinaryChecksum string `yaml:"binary_checksum"` // SHA256 of the extracted binary file
	Binary         string `yaml:"binary"`          // Name of binary to extract from image
	Dest           string `yaml:"dest"`            // Destination path
}

// ReadinessConfig controls how hydra-head decides a spawned llama-engine
// is READY. The old `health` block drove a periodic HTTP poll of llama's
// /slots (or /health) endpoint; that is gone. hydra-head is the *parent*
// of llama-engine, so readiness is event-driven from the child's stdout:
// a sentinel line tells the head the engine is actually serving. A
// miss-deadline (TimeoutSec with no sentinel) marks the process suspect so
// the coordinator sees it is not ready, without the head waking llama up
// with HTTP requests.
type ReadinessConfig struct {
	// Sentinels are substrings matched against llama-engine stdout lines.
	// When any line contains one, the process transitions to StateReady.
	// Defaults cover the fork's lifecycle lines:
	//   "server is listening on" (full server, boot-resident model)
	//   "router server is listening on" (router mode, no boot model)
	//   "model loaded"
	Sentinels []string `yaml:"sentinels"`

	// TimeoutSec is how long to wait for a sentinel after the process
	// starts before marking it suspect (started, not ready). A generous
	// default (180s) covers slow VM disks where model load can take
	// 235-300s (P100). Only used for the miss-deadline — no periodic
	// probing.
	TimeoutSec int `yaml:"timeout_sec"`
}

func Load(globalPath, nodePath string) (*Config, error) {
	globalData, err := os.ReadFile(globalPath)
	if err != nil {
		return nil, fmt.Errorf("read global config: %w", err)
	}

	nodeData, err := os.ReadFile(nodePath)
	if err != nil {
		return nil, fmt.Errorf("read node config: %w", err)
	}

	var global, node Config
	if err := yaml.Unmarshal(globalData, &global); err != nil {
		return nil, fmt.Errorf("parse global config: %w", err)
	}
	if err := yaml.Unmarshal(nodeData, &node); err != nil {
		return nil, fmt.Errorf("parse node config: %w", err)
	}

	merged := mergeConfigs(&global, &node)
	return merged, nil
}

func mergeConfigs(global, node *Config) *Config {
	merged := *node

	if merged.Node.Name == "" {
		merged.Node.Name = global.Node.Name
	}
	if merged.Node.Mode == "" {
		merged.Node.Mode = global.Node.Mode
	}

	if merged.Llama.Binary == "" {
		merged.Llama.Binary = global.Llama.Binary
	}
	if merged.Llama.WorkingDir == "" {
		merged.Llama.WorkingDir = global.Llama.WorkingDir
	}
	if merged.Llama.Host == "" {
		merged.Llama.Host = global.Llama.Host
	}
	if merged.Llama.Port == 0 {
		merged.Llama.Port = global.Llama.Port
	}
	if merged.Llama.RPCPort == 0 {
		merged.Llama.RPCPort = global.Llama.RPCPort
	}

	if merged.Llama.Params == nil {
		merged.Llama.Params = make(map[string]any)
	}
	for k, v := range global.Llama.Params {
		if _, exists := merged.Llama.Params[k]; !exists {
			merged.Llama.Params[k] = v
		}
	}

	if merged.Llama.Env == nil {
		merged.Llama.Env = make(map[string]string)
	}
	for k, v := range global.Llama.Env {
		if _, exists := merged.Llama.Env[k]; !exists {
			merged.Llama.Env[k] = v
		}
	}

	if merged.Infra.Prometheus.Host == "" {
		merged.Infra.Prometheus.Host = global.Infra.Prometheus.Host
	}
	if merged.Infra.Prometheus.Port == 0 {
		merged.Infra.Prometheus.Port = global.Infra.Prometheus.Port
	}
	if merged.Infra.Loki.URL == "" {
		merged.Infra.Loki.URL = global.Infra.Loki.URL
	}
	if merged.Infra.Grafana.URL == "" {
		merged.Infra.Grafana.URL = global.Infra.Grafana.URL
	}
	if merged.Infra.OTel.URL == "" {
		merged.Infra.OTel.URL = global.Infra.OTel.URL
	}

	if !merged.Services.Promtail.Enabled {
		merged.Services.Promtail.Enabled = global.Services.Promtail.Enabled
	}
	if merged.Services.Promtail.Binary == "" {
		merged.Services.Promtail.Binary = global.Services.Promtail.Binary
	}
	if merged.Services.Promtail.Config == "" {
		merged.Services.Promtail.Config = global.Services.Promtail.Config
	}

	if !merged.Services.NodeExporter.Enabled {
		merged.Services.NodeExporter.Enabled = global.Services.NodeExporter.Enabled
	}
	if merged.Services.NodeExporter.Binary == "" {
		merged.Services.NodeExporter.Binary = global.Services.NodeExporter.Binary
	}
	if merged.Services.NodeExporter.Port == 0 {
		merged.Services.NodeExporter.Port = global.Services.NodeExporter.Port
	}

	if !merged.Services.NvidiaExporter.Enabled {
		merged.Services.NvidiaExporter.Enabled = global.Services.NvidiaExporter.Enabled
	}
	if merged.Services.NvidiaExporter.Binary == "" {
		merged.Services.NvidiaExporter.Binary = global.Services.NvidiaExporter.Binary
	}
	if merged.Services.NvidiaExporter.Port == 0 {
		merged.Services.NvidiaExporter.Port = global.Services.NvidiaExporter.Port
	}

	if merged.Binaries == nil {
		merged.Binaries = global.Binaries
	}

	// Readiness: node values override global; empty/zero fields fall
	// through to global. Per-field merge so a node can override only
	// the timeout while inheriting the sentinel defaults.
	if len(merged.Readiness.Sentinels) == 0 {
		merged.Readiness.Sentinels = global.Readiness.Sentinels
	}
	if merged.Readiness.TimeoutSec == 0 {
		merged.Readiness.TimeoutSec = global.Readiness.TimeoutSec
	}
	if len(merged.Readiness.Sentinels) == 0 {
		// Default lifecycle sentinels for the fork's binaries. The
		// llama-engine binary (RTX/RTX3060) emits "hydra-engine ready" at
		// true readiness on all three startup paths (fork PR
		// ddvnguyen/llama.cpp#80). The classic llama-server binary (P100)
		// emits the three server.cpp lines.
		merged.Readiness.Sentinels = []string{
			"hydra-engine ready",
			"server is listening on",
			"router server is listening on",
			"model loaded",
		}
	}
	if merged.Readiness.TimeoutSec == 0 {
		merged.Readiness.TimeoutSec = 180
	}

	// ── Infra merge ───────────────────────────────────────────────────
	// Per-node configs can override these selectively (e.g. P100 points
	// the OTel endpoint at the RTX host's collector: infra.otel.url =
	// http://192.168.122.1:4318). Fields set in the node config stay;
	// blank fields inherit the global default.
	if merged.Infra.Prometheus.URL == "" {
		merged.Infra.Prometheus.URL = global.Infra.Prometheus.URL
	}
	if merged.Infra.Loki.URL == "" {
		merged.Infra.Loki.URL = global.Infra.Loki.URL
	}
	if merged.Infra.Grafana.URL == "" {
		merged.Infra.Grafana.URL = global.Infra.Grafana.URL
	}
	if merged.Infra.OTel.URL == "" {
		merged.Infra.OTel.URL = global.Infra.OTel.URL
	}

	return &merged
}

func (c *Config) BuildLlamaArgs() []string {
	var args []string

	args = append(args, "--host", c.Llama.Host)
	args = append(args, "--port", fmt.Sprintf("%d", c.Llama.Port))
	args = append(args, "--rpc-port", fmt.Sprintf("%d", c.Llama.RPCPort))

	args = append(args, c.buildParamsArgs()...)

	return args
}

// fitParamsKeys are the config keys relevant to llama-fit-params model fitting.
// Only model/context/device parameters that affect VRAM estimation are included.
var fitParamsKeys = map[string]bool{
	"model":                 true,
	"ctx-size":              true,
	"n-gpu-layers":          true,
	"n-cpu-moe":             true,
	"tensor-split":          true,
	"flash-attn":            true,
	"cache-type-k":          true,
	"cache-type-v":          true,
	"no-kv-offload":         true,
	"main-gpu":              true,
	"rope-scaling":          true,
	"rope-scale":            true,
	"yarn-orig-ctx":         true,
	"rpc-engine":            true,
	"override-tensor":       true,
	"tensor-buft-overrides": true,
}

func (c *Config) BuildFitArgs() []string {
	return c.buildParamsArgsFiltered(fitParamsKeys)
}

func (c *Config) buildParamsArgs() []string {
	return c.buildParamsArgsFiltered(nil)
}

// removedParamsKeys are Hydra flags that existed before the v4 merged-RPC-server
// migration (fork `ddvnguyen/llama.cpp#30`/`#37`) but are no-ops on the current
// llama-engine binary — it warns and ignores them instead of rejecting them,
// so a stale config silently misbehaves rather than failing loudly:
//   - "ggml-rpc-port": the separate ggml-RPC backend port is gone. There is now
//     a single unified port (`--rpc-port` / `c.Llama.RPCPort`) that serves both
//     the Hydra state-streaming protocol and ggml-RPC compute dispatch,
//     distinguished by a one-byte MSG_PEEK on the same socket.
//   - "peer-only": compute-only (no-model) mode is now entered implicitly by
//     omitting `model` from params, not by a flag.
//
// These stay valid as Go-side-only config markers (see IsPeerOnly, used for
// health-checker simple mode and fit-preflight skip) but must never reach the
// binary's argv, so they're filtered out here regardless of the `keep` list.
var removedParamsKeys = map[string]bool{
	"ggml-rpc-port": true,
	"peer-only":     true,
}

func (c *Config) buildParamsArgsFiltered(keep map[string]bool) []string {
	var args []string

	keys := make([]string, 0, len(c.Llama.Params))
	for key := range c.Llama.Params {
		keys = append(keys, key)
	}
	sort.Strings(keys)

	for _, key := range keys {
		if removedParamsKeys[key] {
			continue
		}
		if keep != nil && !keep[key] {
			continue
		}
		value := c.Llama.Params[key]
		switch v := value.(type) {
		case bool:
			if v {
				args = append(args, fmt.Sprintf("--%s", key))
			}
		case string:
			args = append(args, fmt.Sprintf("--%s", key), v)
		case int:
			args = append(args, fmt.Sprintf("--%s", key), fmt.Sprintf("%d", v))
		case float64:
			args = append(args, fmt.Sprintf("--%s", key), fmt.Sprintf("%v", v))
		default:
			fmt.Fprintf(os.Stderr, "warning: skipping param %q with unsupported type %T\n", key, value)
		}
	}

	return args
}

func (c *Config) BuildServiceArgs(name string, svc ServiceConfig) []string {
	var args []string
	switch name {
	case "promtail":
		args = append(args, "-config.file", svc.Config)
	case "node_exporter":
		if svc.Port > 0 {
			args = append(args, fmt.Sprintf("--web.listen-address=:%d", svc.Port))
		}
	case "nvidia_exporter":
		if svc.Port > 0 {
			args = append(args, fmt.Sprintf("--web.listen-address=:%d", svc.Port))
		}
	}
	args = append(args, svc.Args...)
	return args
}

func (c *Config) ServiceBinary(name string) string {
	switch name {
	case "promtail":
		return c.Services.Promtail.Binary
	case "node_exporter":
		return c.Services.NodeExporter.Binary
	case "nvidia_exporter":
		return c.Services.NvidiaExporter.Binary
	default:
		return ""
	}
}

func (c *Config) ServiceConfig(name string) ServiceConfig {
	switch name {
	case "promtail":
		return c.Services.Promtail
	case "node_exporter":
		return c.Services.NodeExporter
	case "nvidia_exporter":
		return c.Services.NvidiaExporter
	default:
		return ServiceConfig{}
	}
}

func (c *Config) AllServiceNames() []string {
	return []string{"promtail", "node_exporter", "nvidia_exporter"}
}

func (c *Config) LogLlamaConfig(logger *slog.Logger) {
	logger.Info("llama config",
		"binary", c.Llama.Binary,
		"working_dir", c.Llama.WorkingDir,
		"host", c.Llama.Host,
		"port", c.Llama.Port,
		"rpc_port", c.Llama.RPCPort)

	// Log each merged llama param in a structured field
	keys := make([]string, 0, len(c.Llama.Params))
	for k := range c.Llama.Params {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	attrs := make([]slog.Attr, 0, len(keys))
	for _, k := range keys {
		attrs = append(attrs, slog.Any(k, c.Llama.Params[k]))
	}
	logger.LogAttrs(nil, slog.LevelInfo, "llama params (merged: global + node)", attrs...)
}

// Hydra #383 T3: check if peer-only mode (no model loaded).
func (c *Config) IsPeerOnly() bool {
	v, ok := c.Llama.Params["peer-only"]
	if !ok {
		return false
	}
	b, _ := v.(bool)
	return b
}

// ReadinessSentinels returns the readiness sentinels with defaults applied.
func (c *Config) ReadinessSentinels() []string {
	return c.Readiness.Sentinels
}

// ReadinessTimeout returns the readiness miss-deadline.
func (c *Config) ReadinessTimeout() time.Duration {
	return time.Duration(c.Readiness.TimeoutSec) * time.Second
}

func (c *Config) Validate() error {
	if c.Node.Name == "" {
		return fmt.Errorf("node.name is required")
	}
	if c.Llama.Binary == "" {
		return fmt.Errorf("llama.binary is required")
	}
	if c.Llama.Port == 0 {
		return fmt.Errorf("llama.port is required")
	}
	if c.Llama.RPCPort == 0 {
		// Since the v4 merged-RPC-server migration there is a single unified
		// port for every process (head or peer, model-loaded or compute-only)
		// — it serves both the Hydra state-streaming protocol and ggml-RPC
		// compute dispatch. Peer-only nodes need it just as much as heads do
		// (it's the port the head's --rpc-engine dials), so it's always required.
		return fmt.Errorf("llama.rpc_port is required")
	}
	if c.Readiness.TimeoutSec < 0 {
		return fmt.Errorf("readiness.timeout_sec must be >= 0, got %d", c.Readiness.TimeoutSec)
	}
	// Hydra #383 T3: reject list-param values (must be scalar).
	for key, val := range c.Llama.Params {
		switch val.(type) {
		case []any, []string:
			return fmt.Errorf("llama.params.%s: list/array values are not supported — use scalar values", key)
		}
	}

	// Hydra #383 T3: reject incompatible param combinations.
	splitMode, hasSplit := c.Llama.Params["combined-split-mode"]
	if hasSplit {
		sm, _ := splitMode.(string)
		if sm == "layer" {
			if _, hasOT := c.Llama.Params["combined-ot-pattern"]; hasOT {
				return fmt.Errorf("llama.params: combined-split-mode=layer is incompatible with combined-ot-pattern (use combined-tensor-split instead)")
			}
			if _, hasSplit := c.Llama.Params["combined-tensor-split"]; !hasSplit {
				return fmt.Errorf("llama.params: combined-split-mode=layer requires combined-tensor-split")
			}
		}
	}

	return nil
}

func (c *Config) GeneratePromtailConfig(outputPath string) error {
	if !c.Services.Promtail.Enabled {
		return nil
	}

	lokiURL := c.Infra.Loki.URL
	if lokiURL == "" {
		return fmt.Errorf("infra.loki.url is required for promtail")
	}

	config := fmt.Sprintf(`server:
  http_listen_port: 9080
  grpc_listen_port: 0

positions:
  filename: /tmp/positions.yaml

clients:
  - url: %s/loki/api/v1/push

scrape_configs:
  - job_name: llama-server
    static_configs:
      - targets:
          - localhost
        labels:
          job: llama-server
          node: %s
          __path__: /var/log/hydra/llama-*.log
`, lokiURL, c.Node.Name)

	if err := os.MkdirAll(filepath.Dir(outputPath), 0755); err != nil {
		return fmt.Errorf("create promtail config dir: %w", err)
	}

	if err := os.WriteFile(outputPath, []byte(config), 0644); err != nil {
		return fmt.Errorf("write promtail config: %w", err)
	}

	return nil
}
