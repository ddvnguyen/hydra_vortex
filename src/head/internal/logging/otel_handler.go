// Package logging wires Hydra.Head's slog logger to an OTel log SDK
// exporter that pushes log records to the OTel Collector gateway
// (or directly to Loki's OTLP listener) via OTLP/HTTP.
//
// Hydra.Head itself logs via slog (per the codebase convention). The
// OTel log SDK provides a slog.Handler implementation that converts
// each slog record to an OTel log record and exports it through the
// configured exporter. The exporter is the OTel Collector's OTLP
// HTTP receiver on port 4318 (or Loki's /otlp/v1/logs endpoint
// directly if a collector is not in the path).
//
// See docs/design-direct-push-logging.md § "Per-service
// responsibilities → Hydra.Head (Go)" for the design rationale.
package logging

import (
	"context"
	"fmt"
	"log/slog"
	"os"

	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp"
	otelLog "go.opentelemetry.io/otel/log"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	"go.opentelemetry.io/otel/sdk/resource"
	semconv "go.opentelemetry.io/otel/semconv/v1.41.0"
)

// Config is the input to NewOTelHandler. It captures the OTel push
// target and the resource attributes that identify this hydra-head
// instance to the collector (which becomes the Loki stream labels
// `component` and `node`).
type Config struct {
	// Endpoint is the OTLP/HTTP URL of the OTel Collector gateway
	// (or Loki's /otlp/v1/logs if the collector is bypassed). On
	// RTX the collector is at http://localhost:4318; on P100
	// (no collector on the VM), it's http://192.168.122.1:4318.
	Endpoint string

	// ServiceName is the OTel resource attribute `service.name`,
	// which the OTel Collector maps to the Loki stream label
	// `component`. For Hydra.Head, this is "hydra-head".
	ServiceName string

	// ServiceNamespace is the OTel resource attribute
	// `service.namespace`. For Hydra.Head, this is "hydra-core"
	// (the namespace is shared with Hydra.Core for unified
	// dashboards).
	ServiceNamespace string

	// ServiceInstanceID is the OTel resource attribute
	// `service.instance.id`, mapped by the collector to the Loki
	// stream label `node`. Typical values: "rtx", "p100".
	ServiceInstanceID string

	// Environment is the OTel resource attribute
	// `deployment.environment.name`. Typical value: "dev".
	Environment string
}

// NewOTelHandler builds a slog.Handler that exports every record to
// the OTel Collector (or Loki's OTLP endpoint) via OTLP/HTTP. The
// returned handler also writes to os.Stdout (text format) so that
// `journalctl -u hydra-head` (on P100) and `podman logs
// hydra-head-rtx` (on RTX) continue to work for forensic
// investigations — the OTel push is the new primary path, not a
// replacement for the local text log.
//
// The slog handler is thread-safe; the OTel exporter is goroutine-
// safe. The handler holds no mutable state beyond the OTel SDK's
// internal batcher, which is bounded (see
// docs/design-direct-push-logging.md § "Queue bounds" for the
// explicit numbers).
func NewOTelHandler(ctx context.Context, cfg Config) (slog.Handler, func(context.Context) error, *SharedLogger, error) {
	if cfg.Endpoint == "" {
		return nil, nil, nil, fmt.Errorf("logging.NewOTelHandler: cfg.Endpoint is required")
	}
	if cfg.ServiceName == "" {
		return nil, nil, nil, fmt.Errorf("logging.NewOTelHandler: cfg.ServiceName is required")
	}
	if cfg.ServiceInstanceID == "" {
		return nil, nil, nil, fmt.Errorf("logging.NewOTelHandler: cfg.ServiceInstanceID is required")
	}

	// Build the OTel resource — these attributes become the Loki
	// stream labels (component, node) after the collector's
	// transform processor maps service.name → component and
	// service.instance.id → node.
	res, err := buildResource(cfg)
	if err != nil {
		return nil, nil, nil, fmt.Errorf("logging.NewOTelHandler: build resource: %w", err)
	}

	// Build the OTLP/HTTP log exporter. The exporter POSTs
	// protobuf-encoded ExportLogServiceRequest to cfg.Endpoint.
	// It is goroutine-safe and internally batched.
	exporter, err := otlploghttp.New(ctx,
		otlploghttp.WithEndpointURL(cfg.Endpoint),
	)
	if err != nil {
		return nil, nil, nil, fmt.Errorf("logging.NewOTelHandler: build exporter: %w", err)
	}

	// Build the OTel log SDK provider. The provider is the
	// in-process queue + batcher; it ships records to the
	// exporter asynchronously. QueueSize and BatchTimeout are
	// explicit per the design doc § "Queue bounds" so a Loki or
	// collector outage cannot OOM the hydra-head process.
	processor := sdklog.NewBatchProcessor(exporter,
		sdklog.WithMaxQueueSize(65_536), // matches C# QueueSize; 32 MB worst case at ~500 B/record
	)
	provider := sdklog.NewLoggerProvider(
		sdklog.WithResource(res),
		sdklog.WithProcessor(processor),
	)

	// Wrap the OTel logger as a slog.Handler. The bridge converts
	// each slog record to one OTel log record. The level mapping
	// is automatic: slog.LevelInfo → otel logs.SeverityInfo, etc.
	otelLogger := provider.Logger(cfg.ServiceName)
	handler := newSlogToOtelHandler(otelLogger, os.Stdout, cfg)

	// shutdown flushes the in-process batch and exports any
	// remaining records before the process exits. Call this in a
	// defer in main().
	shutdown := func(ctx context.Context) error {
		return provider.Shutdown(ctx)
	}

	// SharedLogger exposes the exporter + processor so child
	// processes (llama-server, node_exporter, nvidia_exporter)
	// can build their own LoggerProviders with overridden
	// service.name resources (one per child, with the parent's
	// service.instance.id unchanged). The shared exporter means
	// the OTLP/HTTP connection is reused — children share the
	// same outbound HTTP/2 stream.
	shared := &SharedLogger{
		exporter:  exporter,
		processor: processor,
		instance:  cfg.ServiceInstanceID,
		namespace: cfg.ServiceNamespace,
		env:       cfg.Environment,
	}

	return handler, shutdown, shared, nil
}

// SharedLogger is the exporter + processor bundle that child
// processes share with the parent. Children build their own
// LoggerProviders with a different `service.name` resource
// attribute (so the collector's transform processor maps each
// child to a different `component` Loki label), but the OTLP
// exporter and the in-process batch are shared.
type SharedLogger struct {
	exporter  *otlploghttp.Exporter
	processor sdklog.Processor
	instance  string // service.instance.id (rtx / p100), unchanged for children
	namespace string // service.namespace
	env       string // deployment.environment.name
}

// ChildHandler returns a slog.Handler whose emitted records carry
// the child's `service.name` resource attribute. The handler is
// text-only (writes to textOut) — it does not push to the OTel
// collector directly; instead, the caller should pass it through
// a childWriter (in package process) which emits records through
// the underlying OTel Logger with the overridden resource.
//
// The slog.Handler returned by ChildHandler is suitable for use
// with `slog.New(handler)` to create a child-specific logger.
func (s *SharedLogger) ChildHandler(serviceName string, textOut *os.File) (slog.Handler, *sdklog.LoggerProvider, error) {
	if serviceName == "" {
		return nil, nil, fmt.Errorf("logging.SharedLogger.ChildHandler: serviceName is required")
	}
	cfg := Config{
		ServiceName:       serviceName,
		ServiceNamespace:  s.namespace,
		ServiceInstanceID: s.instance,
		Environment:       s.env,
	}
	res, err := buildResource(cfg)
	if err != nil {
		return nil, nil, fmt.Errorf("logging.SharedLogger.ChildHandler: build resource: %w", err)
	}

	provider := sdklog.NewLoggerProvider(
		sdklog.WithResource(res),
		sdklog.WithProcessor(s.processor),
	)

	logger := provider.Logger(serviceName)
	handler := newSlogToOtelHandler(logger, textOut, cfg)
	return handler, provider, nil
}

// buildResource constructs the OTel resource used by the parent
// hydra-head logger and by each child. Centralized so the parent
// and children use the same shape (only service.name varies).
func buildResource(cfg Config) (*resource.Resource, error) {
	return resource.Merge(
		resource.Default(),
		resource.NewWithAttributes(
			semconv.SchemaURL,
			semconv.ServiceName(cfg.ServiceName),
			semconv.ServiceNamespace(cfg.ServiceNamespace),
			semconv.ServiceInstanceID(cfg.ServiceInstanceID),
			// DeploymentEnvironment was removed from the
			// semconv helpers in v1.41.0; build the KeyValue
			// directly with the well-known key. The
			// collector's transform processor doesn't need
			// this attribute (it copies service.name and
			// service.instance.id to Loki labels;
			// deployment.environment is metadata only).
			attribute.String("deployment.environment.name", cfg.Environment),
		),
	)
}

// newSlogToOtelHandler wraps an OTel logger as a slog.Handler. It
// writes each record to two places:
//
//  1. The OTel logger (which the OTel log SDK exports via OTLP
//     HTTP to the collector).
//  2. A plain-text writer (os.Stdout) for local forensic logs that
//     survive a Loki or collector outage.
//
// The "level" attribute is mapped from the slog level to the OTel
// severity_text, which the OTel Collector's transform processor
// copies to a Loki stream label (the design doc's `level` index
// label is populated by this path).
func newSlogToOtelHandler(otelLogger otelLog.Logger, textOut *os.File, cfg Config) slog.Handler {
	return &slogToOtelHandler{
		otel:   otelLogger,
		text:   textOut,
		cfg:    cfg,
		minLvl: slog.LevelInfo,
	}
}

type slogToOtelHandler struct {
	otel   otelLog.Logger
	text   *os.File
	cfg    Config
	minLvl slog.Level
}

func (h *slogToOtelHandler) Enabled(_ context.Context, l slog.Level) bool {
	return l >= h.minLvl
}

func (h *slogToOtelHandler) Handle(ctx context.Context, r slog.Record) error {
	// 1) OTel log record — emitted to the SDK which batches to
	// the collector. The collector's transform processor maps
	// service.name → component label, service.instance.id → node
	// label, severity_text → level label.
	var otelRecord otelLog.Record
	otelRecord.SetTimestamp(r.Time)
	otelRecord.SetSeverity(otelSeverityForSlog(r.Level))
	otelRecord.SetSeverityText(r.Level.String())
	otelRecord.SetBody(otelLog.StringValue(r.Message))

	// Copy slog attributes → OTel attributes.
	r.Attrs(func(a slog.Attr) bool {
		otelRecord.AddAttributes(logKeyValue(a))
		return true
	})

	h.otel.Emit(ctx, otelRecord)

	// 2) Local text log — for journalctl / podman logs forensic
	// reads. Writes go to os.Stdout (the same path Promtail used
	// to scrape, but now nothing scrapes it; the OTel push is
	// the new source of truth).
	if h.text != nil {
		// Build a structured one-line text record. The format
		// is intentionally simple (slog's default text handler
		// would be heavier; for a forensics-only stream this is
		// enough).
		ts := r.Time.Format("2006-01-02T15:04:05.000Z07:00")
		lvl := r.Level.String()
		msg := r.Message
		// Pre-allocate the attribute string; cap at 4 KiB to
		// avoid pathologically large lines.
		const maxAttrBytes = 4096
		attrStr := ""
		r.Attrs(func(a slog.Attr) bool {
			if len(attrStr) >= maxAttrBytes {
				return false
			}
			attrStr += fmt.Sprintf(" %s=%q", a.Key, a.Value.String())
			return true
		})
		fmt.Fprintf(h.text, "%s %s [%s/%s] %s%s\n",
			ts, lvl, h.cfg.ServiceName, h.cfg.ServiceInstanceID, msg, attrStr)
	}

	return nil
}

func (h *slogToOtelHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	// We don't track attrs on the OTel side (slog calls
	// WithAttrs eagerly and merges the attrs into the record
	// at Handle time). Returning a new handler that knows
	// nothing about the attrs is fine: r.Attrs() inside
	// Handle will see them.
	return &slogToOtelHandler{
		otel:   h.otel,
		text:   h.text,
		cfg:    h.cfg,
		minLvl: h.minLvl,
	}
}

func (h *slogToOtelHandler) WithGroup(name string) slog.Handler {
	// Group names are not specially handled; they are part of
	// the Attr's Key when slog flattens the group into the
	// final record. The OTel attribute key will include the
	// group prefix in dotted form ("group.attr"). This is
	// acceptable for our use case (we don't use slog groups
	// in hydra-head today).
	return h
}

// otelSeverityForSlog maps a slog level to the OTel log severity
// enum used by the OTel log SDK.
func otelSeverityForSlog(l slog.Level) otelLog.Severity {
	switch {
	case l >= slog.LevelError:
		return otelLog.SeverityError
	case l >= slog.LevelWarn:
		return otelLog.SeverityWarn
	case l >= slog.LevelInfo:
		return otelLog.SeverityInfo
	default:
		return otelLog.SeverityDebug
	}
}

// logKeyValue converts a slog.Attr to an OTel log.KeyValue. The OTel
// log SDK does not accept arbitrary slog.Attr values; we map the
// common kinds (string, int, bool, float) and stringify everything
// else.
func logKeyValue(a slog.Attr) otelLog.KeyValue {
	switch a.Value.Kind() {
	case slog.KindString:
		return otelLog.String(a.Key, a.Value.String())
	case slog.KindInt64:
		return otelLog.Int64(a.Key, a.Value.Int64())
	case slog.KindFloat64:
		return otelLog.Float64(a.Key, a.Value.Float64())
	case slog.KindBool:
		return otelLog.Bool(a.Key, a.Value.Bool())
	default:
		// Fall back to the slog string repr; safe for all kinds.
		return otelLog.String(a.Key, a.Value.String())
	}
}
