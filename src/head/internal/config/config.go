package config

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sort"

	"gopkg.in/yaml.v3"
)

type Config struct {
	Node     NodeConfig     `yaml:"node"`
	Llama    LlamaConfig    `yaml:"llama"`
	Services ServicesConfig `yaml:"services"`
	Infra    InfraConfig    `yaml:"infra"`
	Binaries BinariesConfig `yaml:"binaries"`
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

	return &merged
}

func (c *Config) BuildLlamaArgs() []string {
	var args []string

	args = append(args, "--host", c.Llama.Host)
	args = append(args, "--port", fmt.Sprintf("%d", c.Llama.Port))
	args = append(args, "--rpc-port", fmt.Sprintf("%d", c.Llama.RPCPort))

	keys := make([]string, 0, len(c.Llama.Params))
	for key := range c.Llama.Params {
		keys = append(keys, key)
	}
	sort.Strings(keys)

	for _, key := range keys {
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
		return fmt.Errorf("llama.rpc_port is required")
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
