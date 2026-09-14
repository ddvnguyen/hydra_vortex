using System.Text.Json;

namespace Hydra.Core.Models;

/// <summary>
/// Worker node configuration — env-var + JSON driven.
/// </summary>
public sealed record WorkerConfig
{
	public string Name { get; init; } = "";
	/// <summary>
	/// Human-readable GPU label for dashboards, e.g. "RTX 5060 Ti" (workers.json
	/// <c>display_name</c>). Falls back to <see cref="Name"/> when unset so the
	/// request_timeline node labels stay meaningful for any config shape.
	/// </summary>
	public string? DisplayName { get; init; }
	public string Host { get; init; } = "";
	public int RpcPort { get; init; }
	public int LlamaRpcPort { get; init; }
	public string LlamaUrl { get; init; } = "";
	public int WorkerType { get; init; } = 3;
	public int Slots { get; init; } = 1;
	public int PrefillPriority { get; init; } = 1;
	public int DecodePriority { get; init; } = 1;
	public float DecodeSpeedTps { get; init; } = 30f;
	public int MaxPrefillTokens { get; init; } = -1;
	public string? RouterModelName { get; init; }
	public string? PrefillModelName { get; init; }
	public string? DecodeModelName { get; init; }

	// ── Two-engine "work together" (PIPELINE + COMBINED) ────────────────
	// Role of this engine in a multi-engine topology: "standalone" (default), "head", "worker".
	public string Role { get; init; } = "standalone";
	// For a head: the Name of the worker engine it recruits as its peer (must exist in Workers).
	public string? PeerWorker { get; init; }
	// Where the head reaches the peer engine for inter-node activations (Hydra HY RPC).
	public string? PeerHost { get; init; }
	public int PeerPort { get; init; }
	// Phase 2a (ddvnguyen/llama.cpp#36): model alias drives
	// ModelRegistry.Resolve(WorkerConfig.ModelAlias) -> EngineConfig.
	// The override-tensor regexes that used to live here (PipelineOtSplit /
	// CombinedOtSplit) and the run_type label ("combined-static" etc.) are
	// gone — they're inside the ModelRegistry now, keyed by alias.
	public string? ModelAlias { get; init; }
	// ── GPU spec (inline "gpu" object in workers.json) ──
	/// <summary>Static hardware properties for this worker's GPU, loaded inline from the worker's "gpu" object in workers.json.</summary>
	public GpuSpec? Gpu { get; init; }
	// Capability flags, refreshed from the engine's EngineInfo health poll.
	public bool PipelineCapable { get; init; }
	public bool CombinedCapable { get; init; }

	public bool CanPrefill => (WorkerType & 1) != 0;
	public bool CanDecode => (WorkerType & 2) != 0;
	public bool IsHead => string.Equals(Role, "head", StringComparison.OrdinalIgnoreCase);
	// Phase 2a: a "peer-only" worker is one with zero slots. It is dedicated
	// to a head and never runs SOLO requests; MultiEngineRouter treats its
	// availability as implicit. Replaces the old WorkerConfig.RunType ==
	// "combined-static-peer" + IsCombinedStatic derived property.
	public bool IsPeerOnly => Slots == 0;

	/// <summary>
	/// Resolved host for llama-engine RPC connections. Extracts the host from
	/// <see cref="LlamaUrl"/> (e.g. "192.168.122.21" from "http://192.168.122.21:8086").
	/// Falls back to <see cref="Host"/> when <see cref="LlamaUrl"/> is not set.
	/// Used by both HealthMonitorService and WorkerSchedulerService to avoid host drift.
	/// </summary>
	public string LlamaRpcHost => !string.IsNullOrWhiteSpace(LlamaUrl)
		&& Uri.TryCreate(LlamaUrl, UriKind.Absolute, out var u) ? u.Host : Host;
}

/// <summary>
/// Coordinator configuration — all values from HYDRA_COORD_* env vars.
/// </summary>
public sealed record CoordinatorConfig
{
	public string Host { get; init; } = Env("HYDRA_COORD_HOST", "0.0.0.0");
	public int Port { get; init; } = EnvInt("HYDRA_COORD_PORT", 9000);
	public string StoreHost { get; init; } = Env("HYDRA_COORD_STORE_HOST", "127.0.0.1");
	public int StorePort { get; init; } = EnvInt("HYDRA_COORD_STORE_PORT", 9500);
	public int HealthPollIntervalS { get; init; } = EnvInt("HYDRA_COORD_HEALTH_POLL_INTERVAL_S", 20);
	public int HealthPollTimeoutS { get; init; } = EnvInt("HYDRA_COORD_HEALTH_POLL_TIMEOUT_S", 30);
	public int HealthMaxFailures { get; init; } = EnvInt("HYDRA_COORD_HEALTH_MAX_FAILURES", 3);
	// Stuck-slot watchdog (#299/C7): a slot reporting is_processing && n_remain==0 for this
	// many consecutive health-poll cycles is counted as stuck (surfaced via NodeInfo.StuckSlots).
	public int StuckSlotCycles { get; init; } = EnvInt("HYDRA_COORD_STUCK_SLOT_CYCLES", 3);
	public float CharsPerToken { get; init; } = float.Parse(Env("HYDRA_COORD_CHARS_PER_TOKEN", "4.0"));
	public int LlamaRequestTimeoutS { get; init; } = EnvInt("HYDRA_COORD_LLAMA_REQUEST_TIMEOUT_S", 1800);
	public int SessionIdleTimeoutS { get; init; } = EnvInt("HYDRA_COORD_SESSION_IDLE_TIMEOUT_S", 3600);
	// Cold/warm routing is gated on the *new prompt* token count (output is ignored):
	//   newPrompt <= AtomicThreshold → single-worker atomic route (no P/D split)
	//   newPrompt <= WarmThreshold   → reuse the warm affinity slot for follow-up turns
	// AtomicThreshold replaces the former AtomicTokenThreshold + SmallRequestBypassThreshold.
	// Back-compat: the legacy HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD env var is honoured as a fallback.
	public int AtomicThreshold { get; init; } =
		EnvInt("HYDRA_COORD_ATOMIC_THRESHOLD", EnvInt("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", 2048));
	public int WarmThreshold { get; init; } = EnvInt("HYDRA_COORD_WARM_THRESHOLD", 5120);
	public double NPastGuardThreshold { get; init; } = double.Parse(Env("HYDRA_COORD_N_PAST_GUARD_THRESHOLD", "0.6"));
	public int NPastGuardTolerance { get; init; } = EnvInt("HYDRA_COORD_N_PAST_GUARD_TOLERANCE", 50);
	public int WorkerErrorThreshold { get; init; } = EnvInt("HYDRA_COORD_WORKER_ERROR_THRESHOLD", 3);
	public string RunMode { get; init; } = Env("HYDRA_COORD_RUN_MODE", "concurrency");
	/// <summary>Scheduler implementation for A/B: "legacy" (WorkerSchedulerService)
	/// or "v2" (WorkerSchedulerV2). The legacy scheduler is always kept intact;
	/// this flag only selects which one backs <c>IWorkerScheduler</c>.</summary>
	public string SchedulerImplementation { get; init; } = Env("HYDRA_SCHEDULER_IMPL", "legacy");
	public bool MixPrecisionEnabled { get; init; } = EnvBool("HYDRA_COORD_MIX_PRECISION_ENABLED", false);
	/// <summary>
	/// When true, allow a KV cache built with model A to be restored into a slot
	/// loaded with model B (warn + proceed). When false (default), such restores
	/// are aborted and the request re-prefills on the correct model.
	/// Issue #289 (M-Perf.9).
	/// </summary>
	public bool AllowCrossModelKvReuse { get; init; } =
		EnvBool("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE", false);
	/// <summary>
	/// When true, skip restoring KV cache from the Store on session follow-up.
	/// The request will do a full re-prefill instead. Useful for COMBINED mode
	/// where cross-device KV restore may be unreliable.
	/// Config: HYDRA_COORD_NO_STORE_KV_RESTORE=true
	/// </summary>
	public bool NoStoreKvRestore { get; init; } = EnvBool("HYDRA_COORD_NO_STORE_KV_RESTORE", false);
	/// <summary>
	/// Comma-separated list of GGUF model file names that Hydra may route to.
	/// When non-empty, any worker whose loaded model (via /v1/models) does not
	/// match an entry is excluded from routing. Empty = allow all (back-compat).
	/// Config: HYDRA_COORD_ALLOWED_MODELS=Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf
	/// </summary>
	public List<string> AllowedModels { get; init; } = EnvList("HYDRA_COORD_ALLOWED_MODELS");
	public bool RawSlot { get; init; } = EnvBool("HYDRA_COORD_RAW_SLOT", false);
	public bool PrefixCheckpointEnabled { get; init; } = EnvBool("HYDRA_COORD_PREFIX_CHECKPOINT_ENABLED", true);
	/// <summary>
	/// When true, a force_mode="solo" request that has a prior KV checkpoint
	/// in the Store (HasStoreState) will restore the full session KV before
	/// issuing PREFILL, enabling shared-prefix detection (engine-side delta
	/// prefill). When false, solo requests always full-re-prefill (legacy
	/// behaviour). Only meaningful when UseLlamaEngine=true.
	/// Config: HYDRA_COORD_SOLO_PREFIX_REUSE_ENABLED
	/// </summary>
	public bool SoloPrefixReuseEnabled { get; init; } = EnvBool("HYDRA_COORD_SOLO_PREFIX_REUSE_ENABLED", true);
	/// <summary>#712: bounded wait (ms) for the previous turn's in-flight bg save
	/// to commit before the next turn's restore reads the store (and before an
	/// evict judges store freshness). On timeout the restore proceeds with a
	/// possibly-stale blob — larger delta prefill, correct output. Two sites
	/// share this value; hermetic tests shrink it to exercise the timeout branch.
	/// Config: HYDRA_COORD_SOLO_SAVE_WAIT_MS</summary>
	public int SoloSaveWaitMs { get; init; } = EnvInt("HYDRA_COORD_SOLO_SAVE_WAIT_MS", 30000);
	public bool WarmSlotVerificationEnabled { get; init; } = EnvBool("HYDRA_COORD_WARM_SLOT_VERIFY", true);
	/// <summary>
	/// Skip Store Get+StatePut round-trip when the session KV is still resident on the
	/// bound worker's slot (warm residency). Goes straight to Prefill with PrefixCacheHit=true.
	/// Safety: the fork's shared-prefix checkpoint mechanism self-corrects stale residency —
	/// worst case is a full prefill (same as cold), never corruption. See #718.
	/// </summary>
	public bool WarmSlotFastPathEnabled { get; init; } = EnvBool("HYDRA_COORD_WARM_SLOT_FAST_PATH", true);
	public bool EnableChunks { get; init; } = EnvBool("HYDRA_COORD_ENABLE_CHUNKS", false);
	public bool UseLlamaEngine { get; init; } = EnvBool("HYDRA_LLAMA_ENGINE", false);
	// ── Two-engine "work together" (default OFF; only meaningful in engine mode) ──
	// PIPELINE = prima.cpp-style layer-window split; COMBINED = ggml expert-split.
	public bool PipelineEnabled { get; init; } = EnvBool("HYDRA_COORD_PIPELINE_ENABLED", false);
	public bool CombinedEnabled { get; init; } = EnvBool("HYDRA_COORD_COMBINED_ENABLED", false);
	// A request recruits a second engine only when its prompt exceeds this many tokens.
	public int MultiEngineThreshold { get; init; } = EnvInt("HYDRA_COORD_MULTI_ENGINE_THRESHOLD", 8192);
	// When both modes are enabled and a request qualifies, which to prefer: "pipeline" | "combined".
	public string MultiEnginePolicy { get; init; } = Env("HYDRA_COORD_MULTI_ENGINE_POLICY", "pipeline");
	/// <summary>Path to the models.json file (HYDRA_COORD_MODELS_FILE). When set, AutoRouter reads model definitions from it.</summary>
	public string? ModelsFile { get; init; } = Environment.GetEnvironmentVariable("HYDRA_COORD_MODELS_FILE");
	/// <summary>Directory containing model GGUF files (HYDRA_COORD_MODELS_DIR). Used by AutoRouter for local path resolution.</summary>
	public string ModelsDir { get; init; } = Env("HYDRA_COORD_MODELS_DIR", "/models");
	public int ChunkSize { get; init; } = EnvInt("HYDRA_STORE_CHUNK_SIZE", 8192) * 1024;
	// hydra#334: chunk size for the engine's STATE_GET socket-stream (CONFIGURE/0x40
	// "state_chunk_size"), sent once per worker connection. The engine itself defaults
	// to 2 MiB if never configured (e.g. legacy binary that doesn't support CONFIGURE).
	public int StateChunkSizeBytes { get; init; } = EnvInt("HYDRA_COORD_STATE_CHUNK_SIZE_KB", 2048) * 1024;
	public string PrefixCheckpointName { get; init; } = Env("HYDRA_COORD_PREFIX_CHECKPOINT_NAME", "system_prompt");
	public List<WorkerConfig> Workers { get; set; } = [];

	public static List<WorkerConfig> LoadWorkers()
	{
		// Canonical: load from a JSON file (compose deploys use this).
		var file = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
		if (!string.IsNullOrWhiteSpace(file))
		{
			if (!File.Exists(file))
				throw new InvalidOperationException(
					$"HYDRA_COORD_CONFIG_FILE={file} does not exist");
			try
			{
				return JsonSerializer.Deserialize<List<WorkerConfig>>(File.ReadAllText(file),
					new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true,
						PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
					}) ?? [];
			}
			catch (JsonException ex)
			{
				throw new InvalidOperationException(
					$"Failed to parse worker config at {file}: {ex.Message}", ex);
			}
		}

		// Legacy: inline JSON env (kept for unit tests and ad-hoc local runs).
		// If both are set, the file path wins — but warn so it's not silent.
		var json = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
		if (!string.IsNullOrWhiteSpace(json))
			return JsonSerializer.Deserialize<List<WorkerConfig>>(json,
				 new JsonSerializerOptions
				 {
					 PropertyNameCaseInsensitive = true,
					 PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
				 }) ?? [];

		// Fallback default worker for testing (single-model, no per-phase
		// model swap). Interpretation (b) of the mix-precision P/D split —
		// see `DevelopmentRunBook.md` → *Mix-Precision P/D Split Semantics*
		// and `specs/rpc-protocol.md` → *Cross-Model KV Safety* for the
		// rationale. Per-phase model names (e.g. prefill_model_name="nano"
		// + decode_model_name="balanced") would have been the old
		// cross-quantization shape, which is mathematically broken (Q3_K KV
		// != Q5_K weights) — the cross-model guard would safely Abort the
		// restore rather than corrupt output, but it's noisy. Align with
		// the production `infra/hydra-core/config/workers.json` shape.
		return new List<WorkerConfig>
		  {
				new()
				{
					Name = "rtx",
					Host = "localhost",
					RpcPort = 9601,
					LlamaUrl = "http://localhost:8080",
					WorkerType = 3,
					Slots = 2,
					PrefillPriority = 1,
					DecodePriority = 2
				},
				new()
				{
					Name = "p100",
					Host = "localhost",
					RpcPort = 9602,
					LlamaUrl = "http://192.168.122.21:8086",
					WorkerType = 2,
					Slots = 1,
					PrefillPriority = 100,
					DecodePriority = 1
				}
		  };
	}

	public void Validate()
	{
		if (Workers.Count == 0) throw new InvalidOperationException("No workers configured");
		foreach (var w in Workers)
		{
			if (string.IsNullOrWhiteSpace(w.Name)) throw new InvalidOperationException("Worker name required");
			if (string.IsNullOrWhiteSpace(w.Host)) throw new InvalidOperationException($"Worker '{w.Name}' host required");
			if (w.RpcPort <= 0) throw new InvalidOperationException($"Worker '{w.Name}' rpc_port required");
			if (!Uri.TryCreate(w.LlamaUrl, UriKind.Absolute, out _)) throw new InvalidOperationException($"Worker '{w.Name}' llama_url invalid");
		}
	}

	private static string Env(string k, string fb) => Environment.GetEnvironmentVariable(k) ?? fb;
	private static int EnvInt(string k, int fb) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : fb;
	private static bool EnvBool(string k, bool fb) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : fb;
	private static List<string> EnvList(string k)
	{
		var raw = Environment.GetEnvironmentVariable(k);
		if (string.IsNullOrWhiteSpace(raw)) return [];
		return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
	}
}

/// <summary>
/// Session routing entry — which node, which slot, KV context size.
/// </summary>
public sealed class SessionEntry
{
	public string SessionId { get; set; } = "";
	public string NodeName { get; set; } = "";
	public int? SlotId { get; set; }
	public int NPast { get; set; }
	public int NPromptTokens { get; set; }
	public bool HasStoreState { get; set; }
	/// <summary>#712: the n_past the store blob was written at (last
	/// <c>MarkStoreState</c> with a known NPast). The evict-save freshness
	/// check compares this against the ledger NPast — equal means the store
	/// already holds the current slot state and a second save is redundant.</summary>
	public int StoreNPast { get; set; }
	public bool SlotFreed { get; set; }
	public string? PrefixHash { get; set; }
	/// <summary>Model alias this session was routed to (warm-session affinity for STEP 0 of AutoRouter).</summary>
	public string? BoundModel { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cached health snapshot for one worker node.
/// </summary>
public sealed class NodeInfo
{
	public string NodeName { get; set; } = "";
	public bool Healthy { get; set; }
	public int SlotsTotal { get; set; }
	public int SlotsIdle { get; set; }
	public int ConsecutiveFailures { get; set; }
	/// <summary>
	/// Consecutive health-poll cycles where the engine INFO (0x41) RPC failed
	/// while the HTTP /slots poll succeeded. A dead RPC/prefill path (e.g. a
	/// ggml_abort zombie still serving HTTP) must flip the node unhealthy
	/// (#635) or the scheduler keeps dispatching prefill into a dying engine.
	/// Reset to 0 on the first successful INFO RPC (engine restarted).
	/// </summary>
	public int RpcConsecutiveFailures { get; set; }
	public DateTime LastCheck { get; set; }
	public int StuckSlots { get; set; }
	public List<SlotInfo> Slots { get; set; } = [];
	/// <summary>
	/// GGUF-file aliases this node's engine preset advertises it can host
	/// (from engine INFO preset_aliases). Empty = unknown / legacy engine.
	/// Used by AutoRouter residency + Router.IsModelAllowed in place of the
	/// removed /v1/models poll (#479/S3).
	/// </summary>
	public HashSet<string> PresetAliases { get; set; } = [];
	/// <summary>
	/// GGUF-file alias of the model currently resident on this node. Best-effort:
	/// learned from the engine's PREFILL response model_alias and stamped onto
	/// the node by the worker scheduler. Empty = unknown (no request seen yet).
	/// Replaces the removed /v1/models CurrentModel poll (#479/S3).
	/// </summary>
	public string CurrentModel { get; set; } = "";
	/// <summary>
	/// GGUF-derived model identity of the model currently resident on this node.
	/// Populated by the worker scheduler from the engine's PREFILL response.
	/// Used by Gate A (DECODE 0x43) to verify the KV cache matches the loaded model.
	/// Empty = unknown (no request seen yet).
	/// </summary>
	public string ModelTokenizer { get; set; } = "";
	public string ModelName { get; set; } = "";
	public string ModelQuant { get; set; } = "";
	public uint ModelCapabilities { get; set; }
	/// <summary>
	/// Engine capabilities advertised via INFO (0x41) health poll.
	/// Includes "merged_decode" when the engine supports the framed DECODE wire format.
	/// </summary>
	public HashSet<string> EngineCapabilities { get; set; } = [];
	/// <summary>
	/// #738: consecutive SUCCESSFUL empty-cap INFO polls. Debounces the
	/// capability clear: one empty-cap blip (observed on P100: a single empty
	/// /info wiped 11 cached caps for ~61s, costing a full-context re-prefill)
	/// retains last-known; the 2nd consecutive empty poll is authoritative and
	/// clears. A FAILED INFO poll carries this value unchanged (neither
	/// increments nor resets); a successful poll with caps resets it to 0.
	/// </summary>
	public int ConsecutiveEmptyCapPolls { get; set; }
}

public sealed class SlotInfo
{
	public int Id { get; set; }
	public bool IsProcessing { get; set; }
	public int NPast { get; set; }
	/// <summary>Remaining tokens to generate, from llama /slots. 0 while processing = stuck candidate.</summary>
	public int NRemain { get; set; }
	public DateTime LastActive { get; set; } = DateTime.UtcNow;
	/// <summary>Consecutive health-poll cycles this slot has looked stuck (is_processing && n_remain==0).</summary>
	public int StuckPollCount { get; set; }
}
