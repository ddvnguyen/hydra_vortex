using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Hydra.Core.Services;

public sealed class WorkerSchedulerService : IWorkerScheduler
{
	private readonly CoordinatorConfig _cfg;
	private readonly ISessionLedger _ledger;
	private readonly IWorkerTracker _tracker;
	private readonly ICompletionProxyService _proxy;
	private readonly IHealthMonitorService _health;
	private readonly Hydra.Shared.RpcClient? _storeClient;
	private Hydra.Shared.RpcClient StoreClient =>
		_storeClient ?? throw new InvalidOperationException("Store RPC client not wired — check coordinator config");
	private readonly IServiceProvider _serviceProvider;
	private readonly ILogger _log;
	private readonly LocalChunkCache? _chunkCache;
	private readonly CancellationTokenSource _cts = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _agentClients = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _llamaRpcClients = new();
	/// <summary>Dedicated per-worker connection for large state transfers
	/// (STATE_GET / STATE_PUT). The engine's RPC protocol is strictly serial
	/// per connection, so a 265 MB BgSave STATE_GET stream must not hold the
	/// main connection's turn — it would stall STATE_META / DECODE RPCs of the
	/// next turn behind the whole stream (#581).</summary>
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _llamaStateRpcClients = new();
	private readonly HashSet<string> _prefixSet = [];

	/// <summary>
	/// Injectable factory for creating RPC clients (agent + llama binary RPC).
	/// Set in tests to return tracking test doubles instead of real sockets.
	/// </summary>
	internal Func<string, int, Hydra.Shared.RpcClient>? AgentClientFactory { get; set; }
	/// <summary>
	/// Optional override for CalculateBusyTimeouts used in tests.
	/// When set, replaces the real wall-clock timeout calculation so busy-retry
	/// loops fail fast (e.g. 100ms stuck timeout instead of 150s).
	/// Signature: (estimatedTokens, modelLoadTimeS) → (stuckMs, slowMs).
	/// </summary>
	internal Func<long, int, (long stuckMs, long slowMs)>? BusyTimeoutOverride { get; set; }

	/// <summary>
	/// Factory delegate for creating LlamaClient instances. Set in tests to
	/// return mock clients that override GetStateMetaAsync for Gate A testing.
	/// </summary>
	internal Func<string, LlamaClient>? LlamaClientFactory { get; set; }
	/// <summary>
	/// Per-probe bound for the stale-unhealthy liveness probe (#592/#597).
	/// Test seam (mirrors BusyTimeoutOverride): lets tests hold a probe in
	/// flight longer than the production 5s bound when exercising coalescing.
	/// </summary>
	internal TimeSpan LivenessProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
	/// <summary>
	/// #635 fix 2: override for the prefill RPC retry backoff. Tests use
	/// near-zero so crash-retry loops fail fast; production defaults to
	/// <see cref="PrefillRetryBackoff"/>. Signature: (retryCount, engineRestarting)
	/// → delay before the next retry attempt.
	/// </summary>
	internal Func<int, bool, TimeSpan>? RetryBackoffOverride { get; set; }
	private readonly ConcurrentDictionary<string, SlotLease> _warmLeases = new();
	private readonly ConcurrentDictionary<string, IPeerReservation> _peerLeases = new();
	private readonly ConcurrentDictionary<string, string> _activeMultiSessions = new();
	private readonly ConcurrentDictionary<string, byte> _streamCompleted = new();
	/// <summary>
	/// Per-worker in-flight liveness probe (#597): coalesces concurrent cold
	/// requests so a burst during a multi-node health blip fires at most ONE
	/// bounded probe per stale-unhealthy worker — everyone else awaits the
	/// same task. Entries are removed when the probe completes, so the next
	/// stale-unhealthy window re-probes fresh health.
	/// </summary>
	private readonly ConcurrentDictionary<string, Lazy<Task>> _probeInFlight = new();

	/// <summary>
	/// Bounded wait for the decode node's STATE_META query in the merged-decode
	/// path (#581). Must exceed the engine's STATE_GET stream window: the fork
	/// streams a 265 MB BgSave synchronously on its inference thread (~9s), and
	/// STATE_META is served from that same thread — a 3s timeout failed
	/// spuriously when a BgSave stream overlapped the next turn, leaving Gate A
	/// with empty model_metadata. 15s covers the stream window while still
	/// failing fast on a genuinely wedged engine.
	/// </summary>
	private static readonly TimeSpan DecodeMetaQueryTimeout = TimeSpan.FromSeconds(15);
	internal readonly ConcurrentDictionary<string, (string WorkerName, int SlotId, string TraceId)> _pendingBgSaves = new();
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _pipelineCts = new();
	internal readonly ConcurrentDictionary<string, WorkItem> _pendingTimelines = new();

	/// <summary>
	/// #470 Tier-4: scheduler-side registry of sessions whose KV chunks were
	/// written to the L1 chunk cache by this scheduler, keyed to the write
	/// time (Stopwatch ticks). The L1's own byte-budget LRU
	/// (LocalFsChunkCache.EvictLRUAsync) is a NO-OP when the L1 is under its
	/// cap — but the L1 shares /mnt/llm-ram with the Store's chunk dir, and a
	/// full mount is exactly when L1 bytes must be freed so a save/push retry
	/// can succeed. This registry lets the ENOSPC path drop the OLDEST
	/// non-in-flight sessions explicitly (LocalChunkCache.ClearAsync) instead
	/// of relying on that no-op. Entries are removed as they are force-evicted;
	/// the dict is size-capped so a long-lived coordinator never leaks.
	/// </summary>
	private readonly ConcurrentDictionary<string, long> _l1SessionSavedAt = new();
	private const int MaxL1TrackedSessions = 1024;
	/// <summary>Upper bound on sessions force-evicted per ENOSPC pass. The L1
	/// is pure cache (~1 GB per session) and the pass runs inside the prefill
	/// stream / store push, so it must stay bounded — evicting the oldest
	/// non-in-flight sessions is normally enough to unblock the retry.</summary>
	private const int MaxL1EnospcEvictions = 8;

	/// <summary>
	/// #470 Tier-4: sessions currently executing a pipeline phase (dispatched
	/// by the evaluator). ENOSPC forced L1 eviction must never clear a session
	/// that is mid-request — its chunks are being written/read right now. Added
	/// at dispatch, removed when the pipeline invocation completes (including
	/// re-enqueue: the item returns to the queue and re-registers on dispatch).
	/// </summary>
	private readonly ConcurrentDictionary<string, byte> _activePipelineSessions = new();

	// ── Unified GPU-aware request queue ──
	// All requests enter here. The evaluator checks GPU availability and
	// dispatches when enough workers are free. Priority ordering ensures
	// post-prefill decode gets the GPU first.
	private readonly SortedSet<QueueItem> _requestQueue = new(Comparer<QueueItem>.Create((a, b) =>
	{
		var cmp = a.Priority.CompareTo(b.Priority);
		if (cmp != 0) return cmp;
		cmp = a.EnqueuedAt.CompareTo(b.EnqueuedAt);
		if (cmp != 0) return cmp;
		return a.Sequence.CompareTo(b.Sequence);
	}));
	private readonly object _queueLock = new();
	private readonly SemaphoreSlim _evaluatorSignal = new(0, int.MaxValue);
	/// <summary>Signal the evaluator when a GPU finishes a request or a new item enters the queue.</summary>
	internal void SignalEvaluator()
	{
		try { _evaluatorSignal.Release(); } catch (SemaphoreFullException) { }
	}

	public WorkerSchedulerService(
		CoordinatorConfig config,
		ISessionLedger ledger,
		IWorkerTracker tracker,
		ICompletionProxyService proxy,
		IHealthMonitorService health,
		Hydra.Shared.RpcClient? storeClient,
		IServiceProvider serviceProvider,
		ILogger log,
		LocalChunkCache? chunkCache = null)
	{
		_cfg = config; _ledger = ledger; _tracker = tracker; _proxy = proxy;
		_health = health; _storeClient = storeClient; _serviceProvider = serviceProvider; _log = log;
		_chunkCache = chunkCache;

		// Every slot release (lease dispose, eviction, cross-node skip, session
		// cleanup) and every node health flip wakes the evaluator so queued
		// items get a fresh capacity/health check.
		_tracker.SlotReleased += SignalEvaluator;
		_health.HealthyChanged += SignalEvaluator;

		if (config.EnableChunks)
		{
			ChunkEngine.CHUNK_SIZE = config.ChunkSize;
			ChunkConstants.ChunkSize = config.ChunkSize;
		}

		log.Information("Scheduler init: workers={Workers} prefiller={Prefill} decoders={Decode} mix={Mix} evaluator=unified",
			string.Join(",", config.Workers.Select(w => w.Name)),
			config.Workers.Count(w => w.CanPrefill),
			config.Workers.Count(w => w.CanDecode),
			config.MixPrecisionEnabled);
	}

	public string? LastDispatchedNode { get; private set; }
	public string? LastDispatchedModel { get; private set; }
	public string? LastDispatchedTokenizer { get; private set; }
	public string? LastDispatchedModelName { get; private set; }
	public string? LastDispatchedModelQuant { get; private set; }
	public uint LastDispatchedModelCapabilities { get; private set; }

	public async Task<ICompletionResult> SubmitAsync(
		Dictionary<string, object> request,
		List<Dictionary<string, object>> messages,
		string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct, int systemPromptTokens = 0)
	{
		var traceId = Router.NewTraceId();
		var item = new WorkItem(request, messages, sessionId, traceId, prefixHash, estimatedTokens, maxTokens);
		item.SystemPromptTokens = systemPromptTokens;
		item.HttpCancellationToken = ct;
		if (request.TryGetValue("force_mode", out var fmRaw))
		{
			var fmStr = fmRaw is string fms ? fms
				: fmRaw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString()
				: null;
			if (!string.IsNullOrWhiteSpace(fmStr))
			{
				item.ForceMode = fmStr;
				_log.Information("force_mode_applied Mode={Mode}", fmStr);
			}
			else
			{
				_log.Warning("force_mode_debug Raw={Raw} Type={Tp}", fmRaw, fmRaw?.GetType().FullName ?? "null");
			}
		}

		// Phase 2b: extract T1 per-request engine overrides from the request
		// body. Mirrors the force_mode extraction above. The C# side has been
		// silently forwarding these to llama-server's HTTP API; the 0x40
		// path is the engine-mode equivalent. Skip when the engine mode is
		// not in use (the legacy HTTP path still handles them).
		if (_cfg.UseLlamaEngine)
		{
			var overrides = EngineRequestOverrides.FromRequest(request);
			if (!overrides.IsEmpty)
			{
				item.RequestOverrides = overrides;
				_log.Information(
					"request_overrides_applied Sid={Sid} Temp={Temp} TopP={TopP} TopK={TopK} NPredict={NP} Seed={Seed}",
					sessionId, overrides.Temperature, overrides.TopP, overrides.TopK,
					overrides.NPredict, overrides.Seed);
			}
		}

		// #470 Fix 1: model-agnostic sessions (no usable `model` field) must be
		// pinned to the configured auto-routing default model (models.json
		// auto_routing.default_model, e.g. "moe-35b-solo") instead of falling
		// through to legacy routing. Legacy routing prefills with whatever
		// model is currently RESIDENT on the picked worker — the PREFILL body
		// carries no model, so the engine serves its resident model (e.g. a
		// leftover dense-27b COMBINED session → the 27B-Coder-MTP GGUF instead
		// of the default MoE). Pin only when the session has no conflicting
		// binding: a session already bound to a DIFFERENT model (established
		// by an earlier explicit request) must keep it — re-routing it
		// mid-conversation would trip the merged-decode Gate A cross-model
		// guard and abort the turn.
		var hasUsableRequestModel = false;
		if (request.TryGetValue("model", out var requestModelRaw))
		{
			hasUsableRequestModel = requestModelRaw is string ms
				? !string.IsNullOrWhiteSpace(ms)
				: requestModelRaw is System.Text.Json.JsonElement mje
					&& mje.ValueKind == System.Text.Json.JsonValueKind.String
					&& !string.IsNullOrWhiteSpace(mje.GetString());
		}
		if (!hasUsableRequestModel
			&& ModelConfigLoader.InstanceOrNull?.GetAutoRoutingPolicy()
				is { Enabled: true } autoPolicy
			&& !string.IsNullOrWhiteSpace(autoPolicy.DefaultModel)
			&& ModelRegistry.RegisteredAliases.Contains(autoPolicy.DefaultModel))
		{
			var boundModel = _ledger.Lookup(sessionId)?.BoundModel;
			if (string.IsNullOrEmpty(boundModel) || boundModel == autoPolicy.DefaultModel)
			{
				item.Request["model"] = autoPolicy.DefaultModel;
				_log.Information("model_agnostic_pinned_to_default Sid={Sid} Model={Model}",
					sessionId, autoPolicy.DefaultModel);
			}
		}

		// Model config routing: when the client sends "hydra-auto" or a known
		// model alias, route through AutoRouter to select the best worker plan.
		// Unknown models are rejected with 400.
		if (request.TryGetValue("model", out var modelRaw))
		{
			var modelStr = modelRaw is string ms ? ms
				: modelRaw is System.Text.Json.JsonElement mje && mje.ValueKind == System.Text.Json.JsonValueKind.String ? mje.GetString()
				: null;
			_log.Information("model_routing_check Sid={Sid} ModelStr={Str}", sessionId, modelStr);
			if (!string.IsNullOrWhiteSpace(modelStr))
			{
				var isAuto = modelStr == "hydra-auto";
				var isReg = ModelRegistry.RegisteredAliases.Contains(modelStr);
				_log.Information("model_routing_decision Sid={Sid} IsAuto={A} IsRegistered={R} Count={C}",
					sessionId, isAuto, isReg, ModelRegistry.RegisteredAliases.Count);
				if (isAuto || isReg)
				{
					try
					{
						var loader = ModelConfigLoader.InstanceOrNull;
						if (loader == null)
						{
							_log.Debug("autoroute_skipped_no_loader Sid={Sid}", sessionId);
							// No models.json loaded — fall through to old routing
						}
						else
						{
						var autoResult = AutoRouter.Resolve(_cfg, loader, _tracker, _health, _ledger,
							sessionId, estimatedTokens, estimatedTokens + maxTokens, modelStr);
						if (autoResult is { } result)
						{
							_log.Information("autoroute_resolved Sid={Sid} Model={Model} Head={Head} Peer={Peer} Decode={Decode} Mode={Mode}",
								sessionId, result.ModelAlias, result.Head.Name,
								result.Peer?.Name ?? "none", result.DecodeWorker?.Name ?? "none",
								result.Mode ?? "solo");
						// Store the resolved routing identity in `model` so
						// downstream paths (PrefillAsync, ForceMultiEnginePlan,
						// DecodeAsync) read the correct alias directly — no
						// __auto_model_alias intermediary needed.
						item.Request["model"] = result.ModelAlias;

						// FIX #443 P0: persist BoundModel on the session ledger so
						// STEP 0 (TryWarmAffinity) pins future turns to this model.
						_ledger.UpdateBoundModel(sessionId, result.ModelAlias);

							// Wire AutoRouter's worker plan directly into the item,
							// bypassing the old threshold-based ClassifyRequestType/ColdRouteAsync.
							// This ensures P/D split (Mode="pd") and COMBINED (Mode="combined")
							// actually use the workers AutoRouter selected.
							if (result.Mode is "pd" or "combined")
							{
								item.ForceMode = result.Mode;
							}
						}
						else
						{
							_log.Information("autoroute_returned_null Sid={Sid} Model={Model}", sessionId, modelStr);
						}
						}
					}
					catch (Exception ex)
					{
						_log.Warning(ex, "autoroute_failed Sid={Sid} Model={Model}", sessionId, modelStr);
					}
				}
				else
				{
					// Unknown model — reject with OpenAI-compatible error
					_log.Warning("unknown_model Sid={Sid} Model={Model}", sessionId, modelStr);
					throw new InvalidOperationException(
						$"model_not_found: '{modelStr}'. Registered models: [{string.Join(", ", ModelRegistry.RegisteredAliases)}], hydra-auto");
				}
			}
		}

		// #470 canonical identity: resolve the requested model identity ONCE
		// at ingress — raw routing key + role-aware prefill/decode GGUF-file
		// aliases — so every payload builder (PREFILL 0x42 body, DECODE 0x43
		// frame, HTTP-proxy body, cold-atomic swap check) consumes the SAME
		// translated aliases and the raw routing key (e.g. "dense-27b-combined")
		// never reaches the engine wire. Request["model"] stays frozen as the
		// raw routing key — no downstream path mutates it (body-level
		// substitution instead). RequestModelString unwraps the JsonElement
		// shape the HTTP body deserializer produces when AutoRouter failed.
		item.ModelIdentity = RequestedModelIdentity.Resolve(
			RequestModelString(item), ModelConfigLoader.InstanceOrNull);

		_log.Information("request_received Sid={Sid} Stream={Stream}", sessionId, item.IsStreaming);

		// Classify the request type based on estimated tokens and session state.
		item.RequestType = ClassifyRequestType(item, estimatedTokens);
		var priority = GetRequestPriority(item.RequestType);
		var queueItem = new QueueItem(item, item.RequestType, priority);
		lock (_queueLock)
		{
			_requestQueue.Add(queueItem);
		}
		SignalEvaluator();

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
		try
		{
			// Streaming: return the chunk enumerable as soon as decode phase produces it
			if (item.IsStreaming)
			{
				return new StreamCompletionResult(
					await item.StreamCompletion.Task.WaitAsync(TimeSpan.FromSeconds(600), linked.Token));
			}
			else
			{
				// Non-streaming: wait for full response
				return new FinalCompletionResult(
					(await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1800), linked.Token))!);
			}
		}
		catch (OperationCanceledException)
		{
			item.Cancel();
			throw;
		}
	}

	public async Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct)
	{
		var traceId = Router.NewTraceId();
		var entry = _ledger.Lookup(sessionId);
		if (entry == null || !entry.HasStoreState)
			throw new InvalidOperationException("Session not migratable");

		var fromNode = entry.NodeName ?? "unknown";
		var targetWorker = _cfg.Workers.FirstOrDefault(w => w.Name == targetNodeName && w.CanDecode)
			?? throw new InvalidOperationException($"Target worker '{targetNodeName}' not found or cannot decode");

		CoordinatorMetrics.MigrationsTotal.WithLabels(fromNode, targetNodeName).Inc();
		// Issue #306 / #299 C5 fix: MigrationLatency was defined but never
		// observed. Wrap the body in a stopwatch (migrateStart) and observe
		// the elapsed seconds on the success path. The pre-check throws above
		// are preconditions (no migration happened) so they are intentionally
		// not observed — only successful migrations land in the histogram.
		var migrateStart = System.Diagnostics.Stopwatch.StartNew();

		var storeKey = $"{sessionId}.kv";
		var storeResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Get,
			storeKey, ReadOnlyMemory<byte>.Empty, traceId, ct);

		if (storeResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			throw new InvalidOperationException($"Store Get failed for migration: {storeResp.Meta}");

		// A5: acquire a real slot on the target instead of assuming slot 0. The
		// lease is held for the StatePut and released in finally so a failed
		// migration can never leak the slot.
		if (!_tracker.TryAcquireSlot(targetWorker.Name, out var slotId, "migrate"))
			throw new InvalidOperationException($"No free slot on target worker '{targetNodeName}' for migration");
		var migrateLease = new SlotLease(targetWorker.Name, slotId, sessionId, LeaseLifetime.Short, _tracker);

		int nPastAfter;
		try
		{
			var llamaRpc = GetStateRpcClient(targetWorker);
			var putResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StatePut,
				slotId.ToString(), storeResp.Payload, traceId, ct);

			// #617/A1: the blind StatePut's response status was NEVER checked
			// (lines 339-347 pre-fix). A non-success means the target slot does
			// not hold the session KV — registering it resident would send the
			// next continuation into a doomed warm decode. Fail the migrate:
			// no ledger register, no migrated=true to the caller.
			if (putResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			{
				_log.Error("migrate_state_put_failed sid={Sid} status={Status} meta={Meta}",
					sessionId, putResp.Status, putResp.Meta);
				throw new InvalidOperationException(
					$"StatePut failed during migration: status={putResp.Status} meta={putResp.Meta}");
			}

			nPastAfter = 0;
			if (putResp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(putResp.Meta);
				nPastAfter = meta?.TryGetValue("n_past", out var n) == true ? n.GetInt32() : 0;
			}
		}
		finally
		{
			await migrateLease.DisposeAsync();
		}

		// #617/A2: keep the ledger pointing at the target node/slot, but mark
		// the entry NON-RESIDENT (SlotFreed=true, exactly what MarkEvicted
		// does on the evict path) so the NEXT request for this session skips
		// warm affinity and re-enters the restore path (RestoreKvAsync —
		// proven to carry blob+manifest metadata through merged DECODE, Gate A
		// passes). A straight warm decode after migrate is a doomed
		// continuation, so the migrate stays successful while the session is
		// re-hydrated on its next turn.
		_ledger.Register(sessionId, targetNodeName, slotId, nPastAfter, entry.PrefixHash);
		_ledger.MarkEvicted(sessionId);
		_log.Information("migrate_continuation_will_restore sid={Sid} node={Node} slot={Slot}",
			sessionId, targetNodeName, slotId);
		// C5: MigrationLatency was defined but never observed — record it now.
		CoordinatorMetrics.MigrationLatency.WithLabels(fromNode, targetNodeName)
			.Observe(migrateStart.Elapsed.TotalSeconds);
		_log.Information("migrate_done Sid={Sid} To={Node} Slot={Slot} NPast={N} LatencyS={L:F3}",
			sessionId, targetNodeName, slotId, nPastAfter, migrateStart.Elapsed.TotalSeconds);

		return new { migrated = true, session_id = sessionId, target = targetNodeName, slot = slotId, n_past = nPastAfter };
	}

	public async Task RunAsync(CancellationToken ct)
	{
		_ = ReportQueueDepthAsync(ct);

		// Single evaluator loop replaces the old classifier + prefill consumer + decode consumer.
		// Event-driven: woken by SignalEvaluator() when a GPU finishes or new request arrives.
		await RunEvaluatorAsync(ct);
	}

	private async Task ReportQueueDepthAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			int queueDepth;
			lock (_queueLock) { queueDepth = _requestQueue.Count; }
			CoordinatorMetrics.MainQueueDepth.Set(queueDepth);
			// Issue #306: surface warm-lease age for the bench suite's S10
			// watchdog validation. The age of the oldest lease is the
			// canary — a value that grows unbounded while the warm-hit
			// rate is 0 means the eviction watchdog is not reclaiming.
			//
			// Race fix (review #307): `_warmLeases.IsEmpty` and
			// `_warmLeases.Values.Min(...)` are not atomic. A lease can be
			// removed between the two calls, which would make Min() throw
			// InvalidOperationException on an empty sequence. The loop
			// is fire-and-forget (`_ = ReportQueueDepthAsync(ct)`), so an
			// unhandled throw here would permanently stop the entire
			// queue-depth + warm-lease metric stream. Use a single LINQ
			// pipeline guarded by `DefaultIfEmpty` so the metric always
			// reports a valid value (now=now when no leases are held).
			var oldest = _warmLeases.Values
				.Select(v => v.CreatedAt)
				.DefaultIfEmpty(System.DateTime.UtcNow)
				.Min();
			var ageSeconds = (System.DateTime.UtcNow - oldest).TotalSeconds;
			CoordinatorMetrics.WarmLeaseMaxAge.Set(
				ageSeconds < 0 ? 0 : ageSeconds);
			await Task.Delay(TimeSpan.FromSeconds(5), ct);
		}
	}

	// ── Request type classification ──

	private RequestType ClassifyRequestType(WorkItem item, int estimatedTokens)
	{
		if (!string.IsNullOrWhiteSpace(item.ForceMode))
		{
			return item.ForceMode.ToLowerInvariant() switch
			{
				"combined" => RequestType.Combined,
				"pd" => RequestType.Prefill,
				_ => RequestType.Atomic,
			};
		}

		var entry = _ledger.Lookup(item.SessionId);
		if (entry != null && entry.HasStoreState && !entry.SlotFreed)
			return RequestType.Solo;

		if (estimatedTokens > _cfg.AtomicThreshold && _cfg.UseLlamaEngine)
			return RequestType.Prefill;

		return RequestType.Atomic;
	}

	private static int GetRequestPriority(RequestType type) => type switch
	{
		RequestType.Decode => 0,
		RequestType.Solo => 10,
		RequestType.Combined => 20,
		RequestType.Atomic => 30,
		RequestType.Prefill => 40,
		_ => 50,
	};

	private void EnqueueRequest(WorkItem item, RequestType type)
	{
		var priority = GetRequestPriority(type);
		var qi = new QueueItem(item, type, priority);
		lock (_queueLock) { _requestQueue.Add(qi); }
		SignalEvaluator();
	}

	// ── Unified evaluator ──

	private async Task RunEvaluatorAsync(CancellationToken ct)
	{
		var sem = new SemaphoreSlim(_cfg.Workers.Count, _cfg.Workers.Count);
		SignalEvaluator();

		while (!ct.IsCancellationRequested)
		{
			await _evaluatorSignal.WaitAsync(ct).ConfigureAwait(false);
			while (_evaluatorSignal.CurrentCount > 0)
				_evaluatorSignal.WaitAsync(TimeSpan.Zero);

			QueueItem[] snapshot;
			lock (_queueLock) { snapshot = _requestQueue.ToArray(); }

			// #635 fix 1/3: run the #592 stale-unhealthy liveness probe once per
			// wake, BEFORE the admission gate, so a queued/retry-pending item is
			// never excluded by a stale unhealthy flag that a direct probe would
			// clear (a dead-engine flag that the engine has since recovered from).
			// The probe is a no-op when nothing is stale-unhealthy (zero extra
			// latency in the common case), coalesces concurrent cold routes, and
			// is bounded by LivenessProbeTimeout. On success it calls MarkHealthy,
			// which fires HealthyChanged → re-signals this loop, so a just-
			// recovered worker is re-checked within this same wake.
			if (snapshot.Length > 0)
				await ProbeStaleUnhealthyWorkersAsync();

			foreach (var qi in snapshot)
			{
				if (ct.IsCancellationRequested) break;

				// Skip requests whose client has disconnected — don't waste
				// a GPU slot on a dead connection. The item may already be
				// holding a lease from an earlier phase (e.g. queued for the
				// SaveDone->PickDecode handoff while still owning
				// PrefillLease) — finalize it so that lease is released
				// instead of leaking (WorkerTracker busy-time never clears).
				if (qi.WorkItem.IsCancelled)
				{
					_log.Information("evaluator_skip_cancelled Sid={Sid}", qi.WorkItem.SessionId);
					lock (_queueLock) { _requestQueue.Remove(qi); }
					await FinalizeAsync(qi.WorkItem, WorkItemState.Cancelled);
					continue;
				}

				// Accurate gate: Atomic requires a prefill-capable worker, so an
				// item with no prefill capacity simply waits in the queue (the
				// evaluator re-checks on every wake) instead of being dispatched
				// to routing that can only fail (the old 30-retry spin).
				if (!CanServeRequest(qi)) continue;

				// #635 fix 3: acquire the pipeline-concurrency slot BEFORE
				// removing the item from the queue. Previously the item was
				// removed first and then blocked on sem.WaitAsync — when the
				// semaphore was exhausted (Workers.Count pipelines in flight)
				// the item was out of the queue but its pipeline had not
				// started, and a retry re-enqueue of the same WorkItem (a new
				// QueueItem) could leave the removed entry effectively
				// stranded (the evaluator parked on the semaphore, the item
				// invisible to every later wake's snapshot). Keeping the item
				// in the queue until its pipeline actually starts means a
				// retry/requeued item is ALWAYS re-snapshotted and re-checked
				// on the next signal (health flip → HealthyChanged, slot
				// release → SlotReleased, both subscribed below).
				await sem.WaitAsync(ct);
				lock (_queueLock) { _requestQueue.Remove(qi); }

				_log.Information("evaluator_dispatch Sid={Sid} Type={Type} Priority={P}",
					qi.WorkItem.SessionId, qi.Type, qi.Priority);

				// #470 Tier-4: mark the session in-flight for the duration of
				// this pipeline invocation so ENOSPC forced L1 eviction never
				// clears a session that is mid-request. Removed in the finally
				// below — a re-enqueue returns from RunItemPipeline, so the
				// item leaves the active set while queued and re-registers on
				// its next dispatch (the lease it may hold across the handoff
				// is not L1-critical: the Store has the durable chunks).
				_activePipelineSessions[qi.WorkItem.SessionId] = 0;

				var scope = _serviceProvider.CreateScope();
				_ = Task.Run(async () =>
				{
					try
					{
						await RunItemPipeline(qi.WorkItem, qi.Type, ct);
					}
					finally
					{
						_activePipelineSessions.TryRemove(qi.WorkItem.SessionId, out _);
						scope.Dispose();
						sem.Release();
						SignalEvaluator();
					}
				}, ct);
			}
		}
	}

	private bool CanServeRequest(QueueItem qi)
	{
		return qi.Type switch
		{
			RequestType.Decode or RequestType.Solo =>
				_cfg.Workers.Any(w => w.CanDecode && _tracker.IsFree(w.Name) && _health.IsHealthy(w.Name)),
			RequestType.Atomic =>
				// Atomic = same-node cold request: the router (ColdRouteAsync)
				// requires a PREFILL-capable free worker (PickBestPrefillWorker
				// = CanPrefill && IsFree && IsHealthy). A decode-only worker
				// (e.g. p100, cp=False) can NOT satisfy an atomic request — the
				// old `|| any CanDecode free worker` escape branch let items
				// pass the gate and then fail routing to None, which spun the
				// retry loop. Gate must mirror the router's accept set.
				_cfg.Workers.Any(w => w.CanPrefill && w.CanDecode && _tracker.IsFree(w.Name) && _health.IsHealthy(w.Name)),
			RequestType.Prefill =>
				_cfg.Workers.Any(w => w.CanPrefill && _tracker.IsFree(w.Name) && _health.IsHealthy(w.Name)),
			RequestType.Combined =>
				// When ForceMode is set (e.g. from AutoRouter), bypass the
				// MultiEngineThreshold gate — ForceMultiEnginePlan has no
				// threshold and ColdRouteAsync handles the actual feasibility.
				// Without this, small-prompt COMBINED requests (e.g. dense-27b-combined)
				// are blocked by MultiEngineRouter.Select's threshold and time out.
				!string.IsNullOrWhiteSpace(qi.WorkItem.ForceMode)
				|| MultiEngineRouter.Select(_cfg, _cfg.Workers, _tracker, _health, qi.WorkItem.EstimatedTokens) != null,
			_ => false,
		};
	}

	// ── Pipeline runner ──

	private static bool IsPrefillState(WorkItemState s) => s switch
	{
		WorkItemState.ModelLoadPrefill or
		WorkItemState.PrefixRestore or
		WorkItemState.Prefill or
		WorkItemState.SaveKv or
		WorkItemState.SaveDone or
		WorkItemState.MarkEvicted => true,
		_ => false,
	};

	// internal (Tests.Core seam): tests drive the loop directly to verify
	// lease release when item.IsCancelled flips between dispatch phases.
	internal async Task RunItemPipeline(WorkItem item, RequestType initialType, CancellationToken ct)
	{
		try
		{
			if (item.State == WorkItemState.None)
			{
				item.RecordPhase("queue_wait_ms");
				CoordinatorMetrics.QueueWaitDuration.Observe(item.Phases["queue_wait_ms"] / 1000.0);
				CoordinatorMetrics.QueueHeadAge.Set(item.Phases["queue_wait_ms"] / 1000.0);
			}

			while (!item.IsCancelled)
			{
				var next = await DispatchAsync(item, ct);

				if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
				{
					await FinalizeAsync(item, next);
					return;
				}

				if (next == WorkItemState.None)
				{
					// No worker could be routed right now. With the accurate
					// admission gate (Atomic requires a prefill-capable worker),
					// this is a genuine transient race (health blip, free-slot
					// contention) that resolves on the next dispatch. Re-enqueue
					// and let the evaluator re-dispatch — the gate prevents the
					// admission/router mismatch that used to spin this loop.
					if (item.NoWorkerRetries < 30)
					{
						item.NoWorkerRetries++;
						_log.Warning("pipeline_no_worker Sid={Sid} Retry={Retry}/30", item.SessionId, item.NoWorkerRetries);
						EnqueueRequest(item, item.RequestType);
						return;
					}
					item.Error = new InvalidOperationException("No worker available");
					await FinalizeAsync(item, WorkItemState.Failed);
					return;
				}

				// Handle Retry BEFORE overwriting item.State — PrefillAsync already
				// reset State to None for re-dispatch; writing Retry would break that.
				if (next == WorkItemState.Retry)
				{
					_log.Information("pipeline_retry Sid={Sid} Retries={R}", item.SessionId, item.RetryCount);
					EnqueueRequest(item, item.RequestType);
					return;
				}

				var prev = item.State;
				item.State = next;
				_log.Information("state_transition Sid={Sid} {Prev}->{Next} ms={Ms}",
					item.SessionId, prev, next, item.ElapsedMs);

				// Prefill→Decode handoff: SaveDone/MarkEvicted → PickDecode
				if (prev is WorkItemState.SaveDone or WorkItemState.MarkEvicted
					&& next == WorkItemState.PickDecode)
				{
					item.RequestType = RequestType.Decode;
					EnqueueRequest(item, RequestType.Decode);
					return;
				}
			}

			// Loop exited because item.IsCancelled flipped true between
			// dispatches (client disconnected — see WorkItem.Cancel(), called
			// from SubmitAsync's own cancellation catch) rather than via a
			// thrown OperationCanceledException. Without this, any lease
			// already acquired (item.PrefillLease/DecodeLease) is never
			// disposed and the WorkerTracker's busy timestamp is never
			// cleared — the worker stays "busy" (hydra_worker_busy_seconds
			// climbs forever) until the coordinator process restarts.
			await FinalizeAsync(item, WorkItemState.Cancelled);
		}
		catch (OperationCanceledException)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "pipeline_crashed Sid={Sid} State={State}", item.SessionId, item.State);
			item.Error = ex;
			await FinalizeAsync(item, WorkItemState.Failed);
		}
	}

	internal async Task<WorkItemState> DispatchAsync(WorkItem item, CancellationToken ct) => item.State switch
	{
		WorkItemState.None => await RouteAsync(item),
		WorkItemState.RouteDecision => await ColdRouteAsync(item),
		WorkItemState.ModelLoadPrefill or WorkItemState.ModelLoadDecode => await ModelLoadAsync(item),
		WorkItemState.PrefixRestore => await PrefixRestoreAsync(item, ct),
		WorkItemState.Prefill => await PrefillAsync(item, ct),
		WorkItemState.SaveKv => await SaveKvAsync(item, ct),
		WorkItemState.SaveDone => await MarkEvictedStateAsync(item),
		WorkItemState.MarkEvicted => await MarkEvictedStateAsync(item),
		WorkItemState.PickDecode => await PickDecodeAsync(item),
		WorkItemState.RestoreKv => await RestoreKvAsync(item, ct),
		WorkItemState.Decode => await DecodeAsync(item, ct),
		WorkItemState.BgSave => await BgSaveAsync(item),
		_ => WorkItemState.Failed
	};

	// ── Gap 2-B + Gap 6: Route with verify warm slot + cross-node affinity ──
	private async Task<WorkItemState> RouteAsync(WorkItem item)
	{
		// Debug force-mode: skip warm affinity when the caller explicitly requests
		// a mode — warm reuse would bypass the cold multi-engine routing entirely.
		if (!string.IsNullOrWhiteSpace(item.ForceMode))
			return await EvictWarmAndColdRouteAsync(item);

		var entry = _ledger.Lookup(item.SessionId);
		item.Entry = entry;

		// Warm affinity — session already has a slot on a node
		if (entry != null && entry.SlotId.HasValue && !entry.SlotFreed)
		{
			// n_tokens guard: if the new prompt is shorter than the previous prompt
			// (not counting thinking/completion tokens), the client sent a truncated
			// history and the KV prefix won't match. Evict and force a cold route.
			// Compare against NPromptTokens (prompt_tokens from last response) rather
			// than NPast (total_tokens including thinking tokens) to avoid false
			// positives caused by Qwen3.5 reasoning tokens being hidden from the client.
			// NPastGuardTolerance is slack on the *shrinkage* side only (estimation
			// noise) — normal turn-over-turn growth must never evict here; the
			// WarmThreshold check below is what caps growth that's too large to
			// warm-prefill cheaply (e.g. on P100).
			var guardBaseline = entry.NPromptTokens > 0 ? entry.NPromptTokens : entry.NPast;
			if (guardBaseline > 0 && item.EstimatedTokens > 0
				&& item.EstimatedTokens + _cfg.NPastGuardTolerance < guardBaseline)
			{
				_log.Warning("n_past_guard Evicted={Sid} Est={Est} GuardBaseline={Past} Tolerance={Tol} NPrompt={NP} NPast={Total} — warm slot would nuke cache",
					item.SessionId, item.EstimatedTokens, guardBaseline, _cfg.NPastGuardTolerance, entry.NPromptTokens, entry.NPast);
				return await EvictWarmAndColdRouteAsync(item);
			}

			// Warm-affinity cap: reuse the warm slot only while the incremental new
			// prompt (vs the cached n_past) stays under WarmThreshold. A large
			// incremental prefill is worth a fresh route — evict and fall through.
			var newPrompt = NewPromptTokens(item, entry);
			if (newPrompt > _cfg.WarmThreshold)
			{
				_log.Information("warm_threshold_exceeded Sid={Sid} NewPrompt={New} NPast={Past} WarmThreshold={WT} — rerouting",
					item.SessionId, newPrompt, entry.NPast, _cfg.WarmThreshold);
				return await EvictWarmAndColdRouteAsync(item);
			}

			var target = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
			if (target != null && _tracker.TryAcquireSlot(target.Name, out var slot, "decode"))
			{
				item.RouteType = "affinity";
				CoordinatorMetrics.RequestsTotal.WithLabels(target.Name, "affinity").Inc();
				CoordinatorMetrics.RequestsTotalAll.Inc();
				CoordinatorMetrics.WarmSessionStarts.Inc();
				item.DecodeWorker = target;
				item.DecodeSlot = slot;
				item.PrefillSlot = entry.SlotId;
				item.DecodeLease = new SlotLease(target.Name, slot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				LastDispatchedNode = target.Name;

				// Verify warm slot before dispatching (skippable via config for testing)
				if (_cfg.WarmSlotVerificationEnabled)
				{
					var isWarm = await Router.VerifyWarmSlotAsync(target, entry, item.TraceId);
					if (!isWarm)
					{
						// ── #718 warm-slot fast path ──
						// The slot verification failed but the session KV may still be
						// resident (transient network blip, stale healthy flag, engine
						// still decoding from the prior turn). Before falling through
						// to the full Store Get+StatePut restore cycle (~30s+), check
						// whether the bound worker is healthy, still serves the same
						// model alias, and has a free prefill slot. If so, go straight
						// to Prefill — the fork's shared-prefix checkpoint mechanism
						// (token-accurate N_COMMON match) self-corrects stale residency:
						// worst case is a full prefill (same as cold), never corruption,
						// because the engine only reuses matching token prefixes and the
						// model-match guard prevents cross-model takeovers.
						// SAFETY: DecodeLease is NOT released here — it mirrors the
						// happy-path warm-affinity branch (L852-864) and cold_atomic's
						// pattern where DecodeLease owns the slot through Prefill→Decode.
						// Releasing before Prefill would hand the physical slot to the
						// next queued request via SignalEvaluator, causing a cross-session
						// slot collision on 1-slot workers.
						if (_cfg.WarmSlotFastPathEnabled
							&& TryWarmSlotFastPath(item, target, entry, "verify_fail"))
						{
							return WorkItemState.Prefill;
						}

						_log.Warning("verify_warm_slot_failed Sid={Sid} Slot={Slot}",
							item.SessionId, entry.SlotId);
						// A1: the slot-state save is a network RPC that can throw. Detach
						// the lease and dispose it in a finally so a save failure can never
						// leak the decode slot.
						var verifyLease = item.DecodeLease;
						item.DecodeLease = null;
						try
						{
							await SaveSlotStateBeforeEvictAsync(item.SessionId, item.DecodeWorker!.Name, item.DecodeSlot ?? 0, item.TraceId, default);
						}
						finally
						{
							await verifyLease.DisposeAsync();
						}
						_ledger.MarkEvicted(item.SessionId);
						item.State = WorkItemState.PickDecode;
						return await PickDecodeAsync(item);
					}
				}

				// N-past guard: estimated tokens too small, force KV restore
				if (entry.NPast > 0 && entry.NPast > _cfg.AtomicThreshold * 4
					&& item.EstimatedTokens < entry.NPast * _cfg.NPastGuardThreshold)
				{
					// ── #718 warm-slot fast path (n_past guard variant) ──
					// The n_past guard says the estimated tokens are too small relative
					// to the cached context, suggesting a short prompt. But the engine
					// may still hold the KV resident — the guard is a heuristic, not a
					// guarantee. Before evicting and falling through to the full Store
					// restore cycle, try the same warm-residency fast path: if the
					// worker is healthy + same model + free prefill slot, go straight
					// to Prefill. The engine's shared-prefix checkpoint self-corrects
					// if the residency is stale (worst case = full prefill, same as cold).
					if (_cfg.WarmSlotFastPathEnabled
						&& TryWarmSlotFastPath(item, target, entry, "npast_guard"))
					{
						return WorkItemState.Prefill;
					}

					// Issue #435: surface how often this predicate evaluates true,
					// even when downstream eviction/save may fail. The {reason}
					// label distinguishes this in-RouteAsync check from any
					// future guards in the same hot path.
					CoordinatorMetrics.WarmSlotEvictedForShortPrompt.WithLabels("warm_slot_n_past_guard").Inc();
					_log.Warning("n_past_guard Sid={Sid} NPast={N} Est={E}",
						item.SessionId, entry.NPast, item.EstimatedTokens);
					// A1: same leak window — detach + dispose in finally around the save RPC.
					var guardLease = item.DecodeLease;
					item.DecodeLease = null;
					_ledger.UpdateNPast(item.SessionId, 0);
					try
					{
						await SaveSlotStateBeforeEvictAsync(item.SessionId, item.DecodeWorker!.Name, item.DecodeSlot ?? 0, item.TraceId, default);
					}
					finally
					{
						await guardLease.DisposeAsync();
					}
					_ledger.MarkEvicted(item.SessionId);
					item.State = WorkItemState.PickDecode;
					return await PickDecodeAsync(item);
				}

				_ledger.UpdateLastUsed(item.SessionId);
				return _cfg.MixPrecisionEnabled && Router.DecodeModel(target) != null
					? WorkItemState.ModelLoadDecode
					: WorkItemState.Decode;
			}

			// Affinity worker busy — try cross-node (Gap 6)
			// #470 (L3): a COMBINED session must never cross-node to a
			// non-CombinedCapable worker (p100) — the KV is layer-split 27B-Coder
			// and p100 would decode it on the 35B engine. Skip the fallback.
			var combinedAff = RequestModelString(item) is { } cam
				&& cam.Contains("combined", StringComparison.OrdinalIgnoreCase);
			var alt = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health,
				exclude: entry.NodeName, allowedModels: _cfg.AllowedModels);
			if (combinedAff && alt is { CombinedCapable: false })
			{
				_log.Warning("cross_node_affinity_skip_combined Sid={Sid} From={From} To={To} — combined session refused non-combined cross-node target",
					item.SessionId, entry.NodeName, alt.Name);
				CoordinatorMetrics.CrossNodeAffinitySkipped.WithLabels(alt.Name, "combined").Inc();
				alt = null;
			}
			if (alt != null && _tracker.TryAcquireSlot(alt.Name, out var altSlot, "decode"))
			{
				// NoStoreKvRestore=true: KV state cannot be transferred between
				// nodes.  A different-node decode worker would have no KV to
				// work with, and the cross-model guard is bypassed (the store-
				// backed hash check never runs).  Skip the cross-node fallback
				// — the request will retry or fail cleanly instead of getting
				// stuck on a worker with incompatible/no KV state.
				if (_cfg.NoStoreKvRestore && alt.Name != entry.NodeName)
				{
					_log.Warning("cross_node_affinity_skip_nokv Sid={Sid} From={From} To={To}",
						item.SessionId, entry.NodeName, alt.Name);
					_tracker.ReleaseSlot(alt.Name, altSlot);
					CoordinatorMetrics.CrossNodeAffinitySkipped.WithLabels(alt.Name, "nokvrestore").Inc();
					return WorkItemState.None;
				}

				item.RouteType = "cross_node";
				CoordinatorMetrics.RequestsTotal.WithLabels(alt.Name, "cross_node").Inc();
				CoordinatorMetrics.RequestsTotalAll.Inc();
				CoordinatorMetrics.CrossNodeAffinityTotal.Inc();
				item.DecodeWorker = alt;
				item.DecodeSlot = altSlot;
				item.DecodeLease = new SlotLease(alt.Name, altSlot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				LastDispatchedNode = alt.Name;
				_log.Information("cross_node_affinity Sid={Sid} From={From} To={To}",
					item.SessionId, entry.NodeName, alt.Name);
				return WorkItemState.RestoreKv;
			}

			return WorkItemState.None;
		}

		// Migration: has store state but no active slot
		if (entry != null && entry.HasStoreState)
		{
			item.RouteType = "migration";
			CoordinatorMetrics.RequestsTotal.WithLabels(entry.NodeName ?? "unknown", "migration").Inc();
			CoordinatorMetrics.RequestsTotalAll.Inc();
			CoordinatorMetrics.MigrationSessionStarts.Inc();

			// COMBINED migration: set PrefillWorker from the session's previous
			// node so PickDecodeAsync's COMBINED guard fires and keeps decode
			// on the same head. Without this, PrefillWorker is null and decode
			// wanders to P100, breaking the dual-GPU binding.
			//
			// NB: MultiMode must ONLY be Combined when the requested model is a
			// combined-mode model. Inferring it from worker.CombinedCapable
			// (hardware capability) broke solo sessions: the second turn of a
			// moe-35b-solo session got MultiMode=Combined → the coordinator
			// sent PIPELINE_ATTACH to the peer → fork stub (#287) → peer
			// declined → fallback crashed with "KV not restored".
			// #470 (L2, investigator 0ef8f152): startup restores the ledger from
			// the Store with EMPTY NodeName (RestoreFromStoreAsync), so an empty
			// NodeName must NOT skip the combined guard — that leaves
			// PrefillWorker=null and PickDecodeAsync's combined guard dead →
			// p100 wins. For a combined request, fall back to the combined head.
			var reqIsCombined = RequestModelString(item) is { } rm
				&& rm.Contains("combined", StringComparison.OrdinalIgnoreCase);
			if (reqIsCombined && string.IsNullOrEmpty(entry.NodeName))
			{
				item.PrefillWorker = _cfg.Workers.FirstOrDefault(w => w.CombinedCapable && w.IsHead);
				if (item.PrefillWorker != null)
					_log.Information("migration_combined_empty_node Sid={Sid} — empty NodeName, pinned PrefillWorker to combined head {Node}",
						item.SessionId, item.PrefillWorker.Name);
			}
			if (!string.IsNullOrEmpty(entry.NodeName))
			{
				item.PrefillWorker = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
				// #470 wedge fix (2026-08-13): check the RAW requested model string for
				// "combined", not the translated prefill alias. TranslateModelAlias maps
				// dense-27b-combined → prefill_alias "qwen3.6-27B-coder" (models.json),
				// which drops the "combined" suffix → reqIsCombined=false → MultiMode
				// stays None → PickDecodeAsync's COMBINED guard never fires →
				// PickBestDecodeWorker wanders to P100 → 27B KV decoded on the 35B
				// engine (wrong-model migration, observed live: trace 4717737069544794,
				// decode_model=Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf, stream_done_no_lease).
				//
				// v2 (investigator 0ef8f152): item.Request["model"] is a JsonElement
				// (raw HTTP body) whenever AutoRouter FAILED — which is exactly when
				// ForceMode stays empty and this migration branch runs. `is string`
				// fails for JsonElement, so use RequestModelString() which unwraps both.
				if (item.PrefillWorker != null
					&& item.PrefillWorker.CombinedCapable
					&& reqIsCombined)
					item.MultiMode = MultiEngineMode.Combined;
			}

			// ── #718 warm-slot fast path (migration interception) ──
			// The solo post-MarkEvicted flow reaches this migration block when
			// SlotFreed=true (set by SaveDone→MarkEvicted). Before falling through
			// to PickDecodeAsync→RestoreKvAsync (30s+ Store Get+StatePut), check
			// if the session's KV is still resident on its bound worker — the
			// engine's live /slots poll is the real residency truth, not SlotFreed.
			// On success: acquire a prefill slot on the bound worker and go straight
			// to Prefill (skip Store round-trip entirely).
			//
			// IMPORTANT: check TryWarmSlotFastPath BEFORE TryAcquireSlot to avoid
			// leaking the slot if the helper rejects (empty BoundModel, model
			// mismatch, slot not in /slots list, etc.).
			if (_cfg.WarmSlotFastPathEnabled
				&& entry.SlotId.HasValue
				&& !string.IsNullOrEmpty(entry.NodeName))
			{
				var target = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
				if (target != null
					&& TryWarmSlotFastPath(item, target, entry, "migration")
					&& _tracker.TryAcquireSlot(target.Name, out var fpSlot, "prefill"))
				{
					item.PrefillWorker = target;
					item.PrefillSlot = fpSlot;
					item.PrefillLease = new SlotLease(target.Name, fpSlot, item.SessionId,
						LeaseLifetime.Short, _tracker);
					return WorkItemState.Prefill;
				}
			}

			item.State = WorkItemState.PickDecode;
			return await PickDecodeAsync(item);
		}

		// Cold path — need prefill
		item.State = WorkItemState.RouteDecision;
		return await ColdRouteAsync(item);
	}

	// New-prompt token count used by warm-affinity gating: for a warm session this
	// is the incremental prompt beyond the cached n_past. Output tokens are ignored.
	// Incremental prompt tokens beyond cached state. Uses NPromptTokens (prompt-side
	// only) when available to avoid inflating the baseline with hidden thinking tokens.
	private static int NewPromptTokens(WorkItem item, SessionEntry? entry)
	{
		if (entry == null) return item.EstimatedTokens;
		var baseline = entry.NPromptTokens > 0 ? entry.NPromptTokens : entry.NPast;
		return baseline > 0 ? Math.Max(0, item.EstimatedTokens - baseline) : item.EstimatedTokens;
	}

	// ── #718 warm-slot fast-path helper ──────────────────────────────────
	// Shared logic for both call sites (verify-fail and n_past-guard). Returns
	// true and mutates item when the fast path is taken; false otherwise.
	//
	// Conditions:
	//   1. nodeInfo != null && nodeInfo.Healthy — bound worker is alive
	//   2. nodeInfo.CurrentModel == entry.BoundModel (OrdinalIgnoreCase) —
	//      exact model match (not Contains — dense-27b vs dense-27b-combined
	//      is a real collision in AutoRouterTests)
	//   3. nodeInfo.Slots contains a SlotInfo whose Id == entry.SlotId —
	//      the engine's /slots poll still lists the session's slot (also
	//      guards engine restart: restarted engine returns empty/different slots)
	//   4. target.CanPrefill — worker is prefill-capable
	//
	// SAFETY: DecodeLease is NOT released. The lease acquired at L852 owns the
	// physical slot through Prefill→Decode, mirroring the happy-path warm-affinity
	// branch and cold_atomic's pattern. Releasing before Prefill would hand the
	// slot to the next queued request via SignalEvaluator, causing a cross-session
	// slot collision on 1-slot workers. The fork's shared-prefix checkpoint
	// mechanism (token-accurate N_COMMON match) self-corrects stale residency —
	// worst case is a full prefill (same as cold), never corruption.
	private bool TryWarmSlotFastPath(WorkItem item, WorkerConfig target, SessionEntry entry, string reason)
	{
		var nodeInfo = _health.GetNodeInfo(target.Name);
		var boundAlias = entry.BoundModel;

		// Gate 1: worker alive
		if (nodeInfo == null || !nodeInfo.Healthy)
			return false;

		// Gate 2: exact case-insensitive model match
		if (string.IsNullOrEmpty(boundAlias)
			|| string.IsNullOrEmpty(nodeInfo.CurrentModel)
			|| !string.Equals(nodeInfo.CurrentModel, boundAlias, StringComparison.OrdinalIgnoreCase))
			return false;

		// Gate 3: engine's /slots poll still lists the session's slot (restart guard)
		if (nodeInfo.Slots == null || nodeInfo.Slots.Count == 0
			|| !nodeInfo.Slots.Any(s => s.Id == entry.SlotId))
			return false;

		// Gate 4: worker is prefill-capable
		if (!target.CanPrefill)
			return false;

		item.RouteType = "warm_slot_fastpath";
		item.PrefixCacheHit = true;
		item.PrefixNPast = entry.NPast;
		item.PrefillWorker = target;
		item.PrefillSlot = entry.SlotId;
		CoordinatorMetrics.RequestsTotal.WithLabels(target.Name, "warm_slot_fastpath").Inc();
		CoordinatorMetrics.RequestsTotalAll.Inc();
		CoordinatorMetrics.WarmSessionStarts.Inc();
		_log.Information("warm_slot_fastpath_{Reason} Sid={Sid} Node={Node} Slot={Slot} NPast={NP} ResidentModel={RM} BoundAlias={BA}",
			reason, item.SessionId, target.Name, entry.SlotId, entry.NPast, nodeInfo.CurrentModel, boundAlias);
		return true;
	}

	private async Task<WorkItemState> EvictWarmAndColdRouteAsync(WorkItem item)
	{
		if (_warmLeases.TryRemove(item.SessionId, out var warmLease))
		{
			await SaveSlotStateBeforeEvictAsync(item.SessionId, warmLease.WorkerName, warmLease.SlotId, item.TraceId, default);
			await warmLease.DisposeAsync();
			SignalEvaluator();
		}
		_ledger.MarkEvicted(item.SessionId);
		item.State = WorkItemState.RouteDecision;
		return await ColdRouteAsync(item);
	}

	private async Task<WorkItemState> ColdRouteAsync(WorkItem item)
	{
		// #592: a stale unhealthy flag (set by health_poll_failed during e.g. an
		// inline model swap, cleared by the next successful poll) must not 503 a
		// request that could be served. When a worker is free + prefill-capable
		// but flagged unhealthy, run one bounded direct liveness probe before
		// excluding it — on success the flag is cleared and every pick below
		// (multi-engine plan, atomic, cold_concurrency) sees fresh health.
		await ProbeStaleUnhealthyWorkersAsync();

		// ── #718 warm-slot fast path (cold-route interception) ──
		// The solo post-MarkEvicted flow reaches ColdRouteAsync when RouteAsync's
		// warm-affinity block is skipped (SlotFreed=true after SaveDone→MarkEvicted)
		// and the migration block falls through to the cold path. Before any cold
		// routing decision, check if the session's KV is still resident on its bound
		// worker — the engine's live /slots poll is the real residency truth, not
		// SlotFreed. If the bound worker is healthy, still serves the same model,
		// and the engine's /slots poll still lists the session's slot → go straight
		// to Prefill, skipping PrefixRestore/Store Get+StatePut entirely.
		if (_cfg.WarmSlotFastPathEnabled)
		{
			var entry = _ledger.Lookup(item.SessionId);
			if (entry != null && entry.SlotId.HasValue)
			{
				var target = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
				if (target != null && TryWarmSlotFastPath(item, target, entry, "cold_route"))
				{
					return WorkItemState.Prefill;
				}
			}
		}

		// Debug force-mode: bypass MultiEngineRouter.Select when the caller
		// sets force_mode in the request body. Handy for testing COMBINED
		// without adjusting the system's threshold/capability config.
		if (!string.IsNullOrWhiteSpace(item.ForceMode))
		{
			var mode = item.ForceMode.ToLowerInvariant();

			// P/D split: prefill on head (RTX), decode selected later by
			// PickDecodeAsync for highest concurrency. ColdRouteAsync only
			// acquires the prefill slot — decode slot is acquired at decode
			// time so the tracker sees the freshest GPU availability.
			if (mode == "pd")
			{
				var pdPfWorker = Router.PickBestPrefillWorker(_cfg.Workers, _tracker, _health, item.EstimatedTokens);
				if (pdPfWorker != null && _tracker.TryAcquireSlot(pdPfWorker.Name, out var pdPfSlot, "prefill"))
				{
					item.RouteType = "cold_pd";
					item.PrefillWorker = pdPfWorker;
					item.PrefillSlot = pdPfSlot;
					item.PrefillLease = new SlotLease(pdPfWorker.Name, pdPfSlot, item.SessionId,
						LeaseLifetime.Short, _tracker);
					LastDispatchedNode = pdPfWorker.Name;
					CoordinatorMetrics.RequestsTotal.WithLabels(pdPfWorker.Name, item.RouteType).Inc();
					CoordinatorMetrics.RequestsTotalAll.Inc();
					CoordinatorMetrics.ColdSessionStarts.Inc();
					_log.Information("cold_pd_route Prefill={Pf} Est={Est}",
						pdPfWorker.Name, item.EstimatedTokens);
					return _cfg.UseLlamaEngine ? WorkItemState.PrefixRestore : WorkItemState.ModelLoadPrefill;
				}
				_log.Warning("cold_pd_route_failed Est={Est}", item.EstimatedTokens);
				// Fall through to normal routing
			}

			var forcePlan = ForceMultiEnginePlan(_cfg, _tracker, _health, item);
			if (forcePlan is { } plan && TryAcquireMultiEnginePrefill(item, plan))
				return _cfg.UseLlamaEngine ? WorkItemState.PrefixRestore : WorkItemState.ModelLoadPrefill;
		}
		else
		{
			// Two-engine "work together": a large request may recruit a second engine.
			var mePlan = MultiEngineRouter.Select(_cfg, _cfg.Workers, _tracker, _health, item.EstimatedTokens);
			if (mePlan is { } plan && TryAcquireMultiEnginePrefill(item, plan))
				return _cfg.UseLlamaEngine ? WorkItemState.PrefixRestore : WorkItemState.ModelLoadPrefill;
		}

		// Cold route: no warm slot/cache to reuse — the chosen worker prefills the
		// full prompt. Gate the single-worker atomic route on the prompt size only
		// (output is ignored). Warm follow-ups are handled in RouteAsync / migration.
		bool atomic = _cfg.RunMode == "fast" || item.EstimatedTokens <= _cfg.AtomicThreshold;

		if (atomic)
		{
			var aw = Router.PickBestAtomicWorker(_cfg.Workers, _tracker, _health, _cfg.AllowedModels);
			_log.Information("cold_atomic_try Est={Est} Worker={W} Free={Free} Healthy={Healthy}",
				item.EstimatedTokens, aw?.Name ?? "none",
				aw != null ? _tracker.IsFree(aw.Name) : false,
				aw != null ? _health.IsHealthy(aw.Name) : false);

			if (aw != null && _tracker.TryAcquireSlot(aw.Name, out var slot, "decode"))
			{
				item.RouteType = "cold_atomic";
				CoordinatorMetrics.RequestsTotal.WithLabels(aw.Name, "cold_atomic").Inc();
				CoordinatorMetrics.RequestsTotalAll.Inc();
				CoordinatorMetrics.ColdSessionStarts.Inc();
				item.DecodeWorker = aw;
				item.DecodeSlot = slot;
				item.DecodeLease = new SlotLease(aw.Name, slot, item.SessionId, LeaseLifetime.Long, _tracker);
				LastDispatchedNode = aw.Name;
				// In engine mode, model is loaded at startup — skip ModelLoadDecode.
				// However, if the requested model differs from the resident model,
				// route through PREFILL first which handles the inline swap
				// (n_predict=0 triggers the swap, then chains to Decode). This
				// avoids the Decode streaming proxy timing out during a 60-120s
				// model swap.
				if (_cfg.UseLlamaEngine)
				{
					var nodeInfo = _health.GetNodeInfo(aw.Name);
					// #470: the canonical identity's DECODE alias drives the
					// swap check (what the engine is running is a GGUF-file
					// alias). The old `is string` read silently returned null
					// for the JsonElement shape (AutoRouter failed) — this
					// never needs the raw request dict at all.
					var requestedAlias = item.ModelIdentity?.DecodeAlias;
					// #470 merged-decode: Gate A validates kv_metadata (what
					// built the KV) against the decode node's model_metadata
					// BEFORE the KV lands. With no PREFILL the KV blob AND the
					// kv_metadata are both empty, so Gate A rejects every cold
					// atomic request ("tokenizer=0 name=0 caps_xor=0xc") and
					// decode aborts with "KV not restored". Routing through
					// PREFILL first (same worker/slot, model already resident
					// → no swap, just a KV-building prefill) produces the
					// identity + KV the merged DECODE requires.
					//
					// PrefillAsync reads item.PrefillWorker/PrefillSlot (not
					// DecodeWorker/DecodeSlot, set above) — without these the
					// PREFILL dispatch null-refs on item.PrefillWorker! before
					// ever reaching the engine. item.PrefillLease is deliberately
					// left null: item.DecodeLease (above) already owns this slot,
					// and SaveKvAsync/PrefillAsync's cleanup paths already
					// null-check PrefillLease before disposing it.
					item.PrefillWorker = aw;
					item.PrefillSlot = slot;
					if (nodeInfo != null
						&& !string.IsNullOrEmpty(requestedAlias)
						&& !string.Equals(nodeInfo.CurrentModel, requestedAlias, StringComparison.OrdinalIgnoreCase))
					{
						_log.Information("cold_atomic_prefill_swap Sid={Sid} Node={N} Resident={R} Requested={Req}",
							item.SessionId, aw.Name, nodeInfo!.CurrentModel, requestedAlias);
					}
					else
					{
						// Model already resident — prefill still required so the
						// merged DECODE has KV + kv_metadata for Gate A (#470).
						_log.Information("cold_atomic_prefill_resident Sid={Sid} Node={N} Model={Model}",
							item.SessionId, aw.Name, nodeInfo?.CurrentModel ?? requestedAlias);
					}
					return WorkItemState.Prefill;
				}
				return WorkItemState.ModelLoadDecode;
			}
		}

		item.RouteType = "cold_concurrency";

		var pfWorker = Router.PickBestPrefillWorker(_cfg.Workers, _tracker, _health, item.EstimatedTokens);
		// If no prefill worker has free slots, evict oldest warm lease to make room
		if (pfWorker == null && _warmLeases.Count > 0)
		{
			var oldest = _warmLeases.OrderBy(kv => kv.Value.CreatedAt).First();
			_log.Information("evicting_warm_slot Sid={Sid} Worker={W} Slot={Slot}",
				oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId);
			await SaveSlotStateBeforeEvictAsync(oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId, item.TraceId, default);
			await oldest.Value.DisposeAsync();
			_warmLeases.TryRemove(oldest.Key, out _);
			SignalEvaluator();
			_ledger.MarkEvicted(oldest.Key);
			pfWorker = Router.PickBestPrefillWorker(_cfg.Workers, _tracker, _health, item.EstimatedTokens);
		}

		_log.Information("cold_route Est={Est} Atomic={Atomic} Route={Route} PrefillWorker={Pw} PrefillFree={Free} PrefillHealthy={Healthy}",
			item.EstimatedTokens, atomic, item.RouteType, pfWorker?.Name ?? "none",
			pfWorker != null ? _tracker.IsFree(pfWorker.Name) : false,
			pfWorker != null ? _health.IsHealthy(pfWorker.Name) : false);

		if (pfWorker == null)
		{
			// Diagnostic: log why no prefill worker found
			foreach (var w in _cfg.Workers.Where(w => w.CanPrefill))
				_log.Warning("cold_route_worker_check Worker={Name} IsFree={F} IsHealthy={H} MaxTokens={MT}",
					w.Name, _tracker.IsFree(w.Name), _health.IsHealthy(w.Name), w.MaxPrefillTokens);
		}

		if (pfWorker != null && _tracker.TryAcquireSlot(pfWorker.Name, out var pfSlot, "prefill"))
		{
			item.RouteType = item.RouteType ?? "cold_concurrency";
			CoordinatorMetrics.RequestsTotal.WithLabels(pfWorker.Name, item.RouteType).Inc();
			CoordinatorMetrics.RequestsTotalAll.Inc();
			CoordinatorMetrics.ColdSessionStarts.Inc();
			item.PrefillWorker = pfWorker;
			item.PrefillSlot = pfSlot;
			item.PrefillLease = new SlotLease(pfWorker.Name, pfSlot, item.SessionId,
				LeaseLifetime.Short, _tracker);
			LastDispatchedNode = pfWorker.Name;
			// In engine mode, model is loaded at startup — skip ModelLoadPrefill
			return _cfg.UseLlamaEngine ? WorkItemState.PrefixRestore : WorkItemState.ModelLoadPrefill;
		}

		_log.Warning("cold_route_no_worker Est={Est} Workers={Workers}", item.EstimatedTokens, string.Join(",", _cfg.Workers.Select(w => $"{w.Name}(cd={w.CanDecode},cp={w.CanPrefill})")));
		return WorkItemState.None;
	}

	// ── Two-engine "work together" ──────────────────────────────────────────

	/// <summary>
	/// Debug: construct a <see cref="MultiEngineRouter.Plan"/> from <c>item.ForceMode</c>,
	/// bypassing the router's threshold and capability checks. Returns null when:
	/// - <c>ForceMode</c> is <c>"solo"</c> or empty
	/// - no suitable head+peer pair is free+healthy for the requested mode
	/// - the mode string is unrecognized
	/// - the head has no <c>ModelAlias</c> resolvable in <see cref="ModelRegistry"/>
	///
	/// Phase 2a: uses <see cref="WorkerConfig.ModelAlias"/> + <see cref="ModelRegistry"/>
	/// instead of the removed <c>CombinedOtSplit</c> / <c>PipelineOtSplit</c> fields.
	/// </summary>
	private MultiEngineRouter.Plan? ForceMultiEnginePlan(CoordinatorConfig cfg, IWorkerTracker tracker, IHealthMonitorService health, WorkItem item)
	{
		var mode = item.ForceMode.ToLowerInvariant();
		if (mode == "solo" || mode == "") return null;
		MultiEngineMode meMode;
		if (mode == "combined")      meMode = MultiEngineMode.Combined;
		else if (mode == "pipeline") meMode = MultiEngineMode.Pipeline;
		else if (mode == "pd")       meMode = MultiEngineMode.Pipeline; // P/D split uses Pipeline routing
		else return null;

		foreach (var head in cfg.Workers
			.Where(w => w.IsHead && tracker.IsFree(w.Name) && health.IsHealthy(w.Name))
			.OrderBy(w => w.PrefillPriority))
		{
			if (string.IsNullOrWhiteSpace(head.PeerWorker)) continue;
			var peer = cfg.Workers.FirstOrDefault(w => w.Name == head.PeerWorker);
			if (peer == null) continue;
			// peer-only workers (slots=0) are always available; others need a free slot.
			if (peer.Slots > 0 && (!tracker.IsFree(peer.Name) || !health.IsHealthy(peer.Name)))
				continue;

			EngineConfig engineConfig;
			// Resolve engine config from the requested model alias (in
			// item.Request["model"]), NOT from head.ModelAlias which is
			// null for model-agnostic workers. Falls back to head.ModelAlias
			// for legacy paths where model is not set on the item.
			// #513: prefer ModelConfigLoader (fresh data-driven config) over
			// ModelRegistry (static hardcoded entries) to avoid stale paths
			// after coordinator restart.
			// #470: the canonical identity's routing key replaces the raw
			// request-dict read — the JsonElement shape (AutoRouter failed)
			// previously fell through to head.ModelAlias and could misresolve
			// the plan's engine config.
			var requestedAlias = item.ModelIdentity?.RoutingKey;
			var resolveAlias = requestedAlias ?? head.ModelAlias ?? "";
			if (string.IsNullOrEmpty(resolveAlias)) continue;
			try
			{
				var loader = ModelConfigLoader.InstanceOrNull;
				if (loader != null)
				{
					try { engineConfig = loader.ResolveEngineConfig(resolveAlias); }
					catch (InvalidOperationException) { engineConfig = ModelRegistry.Resolve(resolveAlias); }
				}
				else
				{
					engineConfig = ModelRegistry.Resolve(resolveAlias);
				}
			}
			catch (InvalidOperationException) { continue; }

			// PIPELINE needs a runtime override-tensor for the engine to route
			// anything to the peer; without one the plan would silently degrade
			// to solo after already reserving the peer. Refuse it here instead.
			if (meMode == MultiEngineMode.Pipeline &&
				(engineConfig.OverrideTensors is not { Length: > 0 } ots || !ots.Any(s => !string.IsNullOrWhiteSpace(s))))
				continue;

			_log.Information("force_multiengine Mode={Mode} Head={Head} Peer={Peer} Alias={Alias}",
				mode, head.Name, peer.Name, engineConfig.ModelAlias);
			return new MultiEngineRouter.Plan(head, peer, meMode, engineConfig);
		}
		return null;
	}

	/// <summary>
	/// Reserve the head slot and hold the peer GPU exclusively for a multi-engine request,
	/// and stamp the plan onto the item. The peer is held with <c>TryReserveWorkerExclusive</c>
	/// (per-GPU, no slot), enforcing principle P1 (one GPU = one task) — no other request,
	/// SOLO or another COMBINED/PIPELINE, can dispatch compute onto the peer while the head is
	/// driving it. If the peer isn't fully idle, admission fails and the caller falls back
	/// to the normal SOLO route. This is the primary resolution for the concurrent-load
	/// CUDA crash (ddvnguyen/llama.cpp#21) by construction.
	///
	/// #368: also refuses if the head OR the peer is in SWAPPING. The SWAPPING
	/// state is mutually exclusive with COMBINED_SERVING (a peer that's about to
	/// free+reload its resident tensors must not be bound to a head, and a head
	/// that's swapping must not lend its slots to a peer).
	/// </summary>
	private bool TryAcquireMultiEngine(WorkItem item, MultiEngineRouter.Plan plan)
	{
		if (_tracker.IsSwapping(plan.Head.Name) || _tracker.IsSwapping(plan.Peer.Name))
			return false;
		if (!_tracker.TryAcquireSlot(plan.Head.Name, out var headSlot, "decode"))
			return false;
		if (!_tracker.TryReserveWorkerExclusive(plan.Peer.Name))
		{
			_tracker.ReleaseSlot(plan.Head.Name, headSlot);
			return false;
		}

		var modeStr = ModeLabel(plan.Mode);
		item.RouteType = $"cold_{modeStr}";
		item.MultiMode = plan.Mode;
		item.MultiPeer = plan.Peer.Name;
		item.MultiEngineConfig = plan.EngineConfig;
		item.DecodeWorker = plan.Head;
		item.DecodeSlot = headSlot;
		item.DecodeLease = new SlotLease(plan.Head.Name, headSlot, item.SessionId, LeaseLifetime.Long, _tracker);
		item.PeerLease = new ExclusivePeerReservation(plan.Peer.Name, _tracker);
		LastDispatchedNode = plan.Head.Name;

		CoordinatorMetrics.RequestsTotal.WithLabels(plan.Head.Name, item.RouteType).Inc();
		CoordinatorMetrics.RequestsTotalAll.Inc();
		CoordinatorMetrics.ColdSessionStarts.Inc();
		CoordinatorMetrics.MultiEngineAttempts.WithLabels(plan.Head.Name, modeStr).Inc();
		_log.Information("multiengine_route Sid={Sid} Mode={Mode} Head={Head} HeadSlot={HS} Peer={Peer} ModelAlias={Alias} Split={Split} Est={Est}",
			item.SessionId, modeStr, plan.Head.Name, headSlot, plan.Peer.Name,
			plan.EngineConfig.ModelAlias, plan.EngineConfig.OverrideTensors?.FirstOrDefault() ?? "",
			item.EstimatedTokens);
		return true;
	}

	/// <summary>
	/// Like <see cref="TryAcquireMultiEngine"/> but acquires a PREFILL slot on the head
	/// instead of a decode slot. Used by COMBINED mode to go through the normal
	/// PrefixRestore → Prefill → SaveKv → Decode flow, so the PREFILL RPC (0x42)
	/// carries model metadata to the engine. The decode slot is acquired later in
	/// PickDecodeAsync after the prefill completes.
	/// </summary>
	private bool TryAcquireMultiEnginePrefill(WorkItem item, MultiEngineRouter.Plan plan)
	{
		if (_tracker.IsSwapping(plan.Head.Name) || _tracker.IsSwapping(plan.Peer.Name))
			return false;
		if (!_tracker.TryAcquireSlot(plan.Head.Name, out var headSlot, "prefill"))
			return false;
		if (!_tracker.TryReserveWorkerExclusive(plan.Peer.Name))
		{
			_tracker.ReleaseSlot(plan.Head.Name, headSlot);
			return false;
		}

		var modeStr = ModeLabel(plan.Mode);
		item.RouteType = $"cold_{modeStr}";
		item.MultiMode = plan.Mode;
		item.MultiPeer = plan.Peer.Name;
		item.MultiEngineConfig = plan.EngineConfig;
		item.PrefillWorker = plan.Head;
		item.PrefillSlot = headSlot;
		item.PrefillLease = new SlotLease(plan.Head.Name, headSlot, item.SessionId, LeaseLifetime.Short, _tracker);
		item.PeerLease = new ExclusivePeerReservation(plan.Peer.Name, _tracker);
		LastDispatchedNode = plan.Head.Name;

		CoordinatorMetrics.RequestsTotal.WithLabels(plan.Head.Name, item.RouteType).Inc();
		CoordinatorMetrics.RequestsTotalAll.Inc();
		CoordinatorMetrics.ColdSessionStarts.Inc();
		CoordinatorMetrics.MultiEngineAttempts.WithLabels(plan.Head.Name, modeStr).Inc();
		_log.Information("multiengine_prefill_route Sid={Sid} Mode={Mode} Head={Head} HeadSlot={HS} Peer={Peer} ModelAlias={Alias} Split={Split} Est={Est}",
			item.SessionId, modeStr, plan.Head.Name, headSlot, plan.Peer.Name,
			plan.EngineConfig.ModelAlias, plan.EngineConfig.OverrideTensors?.FirstOrDefault() ?? "",
			item.EstimatedTokens);
		return true;
	}

	/// <summary>
	/// Activate the chosen multi-engine mode on the head just before decode. COMBINED flips the
	/// MoE expert tensors onto the peer's RPC backend (EngineSetExpertMode); PIPELINE attaches the
	/// peer and hands it its --override-tensor split (EnginePipelineAttach). Any failure — RPC
	/// error, or the engine reporting it stayed solo — degrades transparently to solo decode.
	///
	/// Phase 2a (ddvnguyen/llama.cpp#36): the wire payloads are unchanged. The C# side
	/// translates the <see cref="EngineConfig"/> carried on the plan (item.MultiEngineConfig)
	/// to the existing 0x44/0x46 wire shapes via <see cref="TranslateToWirePayloadAsync"/>.
	/// </summary>
	private async Task ApplyMultiEngineAsync(WorkItem item, WorkerConfig head, int slotId, CancellationToken ct)
	{
		if (item.MultiMode == MultiEngineMode.None) return;
		var modeStr = ModeLabel(item.MultiMode);
		var llamaRpc = GetLlamaRpcClient(head);
		try
		{
			// Phase 2b (#481): for COMBINED, the hydra_config dict is
			// prepared by TranslateToWirePayloadAsync and merged into the
			// PREFILL body at PrefillAsync time. ApplyMultiEngineAsync
			// re-sends the config via PREFILL to activate the mode at
			// decode time.
			// PIPELINE: keep the legacy 0x46 EnginePipelineAttach path.
			var hydraConfig = TranslateToWirePayloadAsync(item);
			if (hydraConfig is not null)
			{
				// Skip if PrefillAsync already delivered hydra_config via a
				// real-content PREFILL — sending a second empty-body PREFILL
				// with ~0 new tokens risks invalidating the KV cache (the
				// n_tokens > n_past invariant). The auto-multiengine path
				// (MultiEngineRouter.Select → Decode directly) never sets
				// this flag, so it still gets its activation PREFILL here.
				if (item.HydraConfigDeliveredSucceeded.HasValue)
				{
					if (item.HydraConfigDeliveredSucceeded.Value)
					{
						RecordMultiEngineActive(item, head, modeStr);
						_log.Debug("multiengine_config_skip Sid={Sid} Mode={Mode} — already delivered via PrefillAsync (success)",
							item.SessionId, modeStr);
					}
					else
					{
						RecordMultiEngineFallback(item, head, modeStr, "prefill_model_fallback",
							"PrefillAsync fell back, skipping redundant empty-body PREFILL");
						_log.Warning("multiengine_config_skip_fallback Sid={Sid} Mode={Mode} — PrefillAsync fell back, skipping redundant empty-body PREFILL",
							item.SessionId, modeStr);
					}
					item.HydraConfigDeliveredSucceeded = null;
					return;
				}
				var engine = new HydraEngineClient(llamaRpc);
				var body = new Dictionary<string, object> { ["messages"] = Array.Empty<object>() };
				var requestJson = System.Text.Json.JsonSerializer.Serialize(body);
				var result = await engine.EnginePrefillAsync(
					slotId, null, requestJson, item.TraceId, ct, hydraConfig);

				if (result is not null && !result.NotImplemented)
					RecordMultiEngineActive(item, head, modeStr);
				else
					RecordMultiEngineFallback(item, head, modeStr, "peer_declined",
						result?.ToString() ?? "null");
			}
			else
			{
				// PIPELINE: legacy 0x46 path
				var addr = !string.IsNullOrWhiteSpace(head.PeerHost)
					? $"{head.PeerHost}:{head.PeerPort}"
					: ResolvePeerAddr(item.MultiPeer);
				var otSplit = item.MultiEngineConfig?.OverrideTensors?.FirstOrDefault() ?? "";
				var resp = await llamaRpc.EnginePipelineAttachAsync(slotId.ToString(), addr, otSplit, item.TraceId, ct);

				if (resp.Status == (byte)StatusCode.Ok && !ReportsSolo(resp.Meta))
					RecordMultiEngineActive(item, head, modeStr);
				else
					RecordMultiEngineFallback(item, head, modeStr, "peer_declined",
						$"Status={resp.Status} Meta={resp.Meta}");
			}
		}
		catch (Exception ex)
		{
			item.MultiFellBack = true;
			CoordinatorMetrics.MultiEngineFallback.WithLabels(head.Name, modeStr, "exception").Inc();
			CoordinatorMetrics.EnginePeerUp.WithLabels(head.Name, item.MultiPeer ?? "").Set(0);
			_log.Warning(ex, "multiengine_activate_error Sid={Sid} Mode={Mode}", item.SessionId, modeStr);
		}
	}

	/// <summary>Record successful multi-engine activation: increment counters, track session, log.</summary>
	private void RecordMultiEngineActive(WorkItem item, WorkerConfig head, string modeStr)
	{
		CoordinatorMetrics.MultiEngineActive.WithLabels(head.Name, modeStr).Inc();
		CoordinatorMetrics.MultiEngineActiveSessions.WithLabels(modeStr).Inc();
		CoordinatorMetrics.EnginePeerUp.WithLabels(head.Name, item.MultiPeer ?? "").Set(1);
		_activeMultiSessions[item.SessionId] = modeStr;
		_log.Information("multiengine_active Sid={Sid} Mode={Mode} Head={Head} Peer={Peer}",
			item.SessionId, modeStr, head.Name, item.MultiPeer);
	}

	/// <summary>Record multi-engine fallback: mark item, increment counters, log.</summary>
	private void RecordMultiEngineFallback(WorkItem item, WorkerConfig head, string modeStr,
		string reason, string? resultDetail = null)
	{
		item.MultiFellBack = true;
		CoordinatorMetrics.MultiEngineFallback.WithLabels(head.Name, modeStr, reason).Inc();
		CoordinatorMetrics.EnginePeerUp.WithLabels(head.Name, item.MultiPeer ?? "").Set(0);
		_log.Warning("multiengine_fallback Sid={Sid} Mode={Mode} Reason={Reason} Detail={Detail}",
			item.SessionId, modeStr, reason, resultDetail ?? "null");
	}

	/// <summary>
	/// Phase 2b (#481): prepare the hydra_config dict that will be injected
	/// into the PREFILL wire body. Returns null for SOLO/ATOMIC (no config
	/// injection), or a populated dict for COMBINED. PIPELINE returns null (uses legacy 0x46 path).
	///
	/// This is a PREPARATION step only — it does NOT call any RPC. The caller
	/// (PrefillAsync) merges the returned dict into the request body and calls
	/// EnginePrefillAsync ONCE with the hydra_config key.
	///
	/// For COMBINED: the dict comes from <see cref="EngineConfig.ToHydraConfigDict"/>
	/// which already emits split_mode, tensor_split, rpc_servers (as JSON array),
	/// model_path, etc.
	///
	/// For PIPELINE: returns the peer address and override tensor regex.
	/// </summary>
	private Dictionary<string, object>? TranslateToWirePayloadAsync(
		WorkItem item)
	{
		if (item.MultiMode == MultiEngineMode.Combined)
		{
			var dict = item.MultiEngineConfig?.ToHydraConfigDict();
			// Gap-1 fix: models.json rpc_servers name workers by logical name
			// ("rtx3060:9504"), which is not resolvable from the head engine's
			// network namespace (host networking, no DNS entry). Translate to
			// the reachable host:port from workers.json so the fork's
			// apply_t3_rebuild() can register the layer-split peer device.
			if (dict is { } d && d.TryGetValue("rpc_servers", out var raw)
				&& raw is string[] endpoints)
			{
				d["rpc_servers"] = MultiEngineRouter.ResolveRpcServerEndpoints(endpoints, _cfg.Workers);
			}
			return dict;
		}
		// PIPELINE: keep the existing 0x46 EnginePipelineAttach path.
		return null;
	}

	/// <summary>
	/// Phase 2b: push per-request T1 overrides (sampling, n_predict,
	/// seed, stop) to the engine via 0x40 EngineConfigure. T1 keys
	/// apply immediately; T2/T3 keys (if any sneak in) are deferred
	/// to the engine's next slot-free moment. Best-effort: any
	/// failure is logged + countered, the request continues with the
	/// engine's current config (same fall-back pattern as
	/// SET_EXPERT_MODE).
	/// </summary>
	private async Task ApplyRequestOverridesAsync(WorkItem item, WorkerConfig head, int slotId, CancellationToken ct)
	{
		if (item.RequestOverrides is not { } overrides || overrides.IsEmpty) return;
		var llamaRpc = GetLlamaRpcClient(head);
		try
		{
			var json = overrides.ToWireJson();
			var resp = await llamaRpc.EngineConfigureAsync(slotId.ToString(), json, item.TraceId, ct);
			var result = HydraEngineClient.ParseConfigureResponse(resp);
			if (result.Success)
			{
				if (result.HasDeferredChanges)
				{
					_log.Information(
						"request_overrides_deferred Sid={Sid} Head={Head} Tier={Tier} Deferred={Deferred}",
						item.SessionId, head.Name, result.Tier,
						string.Join(",", result.DeferredKeys));
				}
				else
				{
					_log.Information(
						"request_overrides_applied Sid={Sid} Head={Head} Tier={Tier} Applied={Applied}",
						item.SessionId, head.Name, result.Tier,
						string.Join(",", result.ParamsApplied.Keys));
				}
			}
			else
			{
				_log.Warning(
					"request_overrides_failed Sid={Sid} Head={Head} Error={Error}",
					item.SessionId, head.Name, result.Error ?? "(no error message)");
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex,
				"request_overrides_exception Sid={Sid} Head={Head}",
				item.SessionId, head.Name);
		}
	}

	private string ResolvePeerAddr(string? peerName)
	{
		var peer = _cfg.Workers.FirstOrDefault(w => w.Name == peerName);
		if (peer == null) return peerName ?? "";
		var host = !string.IsNullOrWhiteSpace(peer.PeerHost) ? peer.PeerHost! : new Uri(peer.LlamaUrl).Host;
		var port = peer.PeerPort > 0 ? peer.PeerPort : peer.LlamaRpcPort;
		return $"{host}:{port}";
	}

	private static bool ReportsSolo(string? meta)
	{
		if (string.IsNullOrWhiteSpace(meta)) return false;
		try
		{
			var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(meta);
			foreach (var key in new[] { "mode", "expert_mode" })
				if (m?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.String
					&& string.Equals(v.GetString(), "solo", StringComparison.OrdinalIgnoreCase))
					return true;
			if (m?.TryGetValue("peer_connected", out var pc) == true && pc.ValueKind == JsonValueKind.False)
				return true;
		}
		catch { }
		return false;
	}

	private async Task ReleasePeerLeaseAsync(string sessionId, IPeerReservation lease)
	{
		await lease.DisposeAsync();
		if (_activeMultiSessions.TryRemove(sessionId, out var modeStr))
			CoordinatorMetrics.MultiEngineActiveSessions.WithLabels(modeStr).Dec();
	}

	private static string ModeLabel(MultiEngineMode mode) =>
		mode == MultiEngineMode.Pipeline ? "pipeline" : "combined";

	/// <summary>Working-together status surfaced on the response (and in /status) for observability.</summary>
	internal static Dictionary<string, object> MultiEngineStatus(WorkItem item)
	{
		var cfg = item.MultiEngineConfig;
		var rpcServers = cfg?.RpcServers;
		var peerCount = rpcServers is { Length: > 0 } ? rpcServers.Length : 0;
		// Phase 2b (#481): use actual SplitMode/TensorSplit from the
		// EngineConfig instead of the legacy OverrideTensors.FirstOrDefault().
		var split = cfg?.SplitMode ?? "";
		if (string.IsNullOrEmpty(split) && cfg?.TensorSplit is { Length: > 0 })
			split = string.Join(",", cfg.TensorSplit.Select(t => t.ToString()));
		if (string.IsNullOrEmpty(split))
			split = cfg?.OverrideTensors?.FirstOrDefault() ?? "";
		return new Dictionary<string, object>
		{
			["engine_mode"] = item.MultiFellBack ? "solo" : ModeLabel(item.MultiMode),
			["requested_mode"] = ModeLabel(item.MultiMode),
			["peer"] = item.MultiPeer ?? "",
			["model_alias"] = cfg?.ModelAlias ?? "",
			["split"] = split,
			["peer_count"] = peerCount,
			["fell_back"] = item.MultiFellBack
		};
	}

	/// <summary>
	/// P3.0+ / #368: try to enter the SWAPPING state on the named worker and
	/// dispatch SWAP_QUANT (0x45). Admission: refuses if the worker has any
	/// slot rented, is exclusively reserved (COMBINED_SERVING), is already
	/// SWAPPING, or is unhealthy. The state transition is atomic (under the
	/// same lock the rest of the worker-state guards use); the actual RPC
	/// is best-effort (stub on the C++ side until #263) and the worker exits
	/// SWAPPING in <c>finally</c> so a hung engine can't strand the GPU.
	/// On exit, SwapGeneration is bumped, which marks outstanding peer
	/// bindings as potentially-stale (the next head epoch-check will see the
	/// bump on the C++ side and rebind).
	/// </summary>
	public async Task<bool> TrySwapQuantAsync(string workerName, string quantKey, string tensorPattern, string traceId, CancellationToken ct)
	{
		if (!_tracker.TryEnterSwapping(workerName))
		{
			_log.Warning("swap_quant_refused Worker={Worker} Reason=busy_or_swapping_or_reserved", workerName);
			return false;
		}
		var sw = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			_llamaRpcClients.TryGetValue(workerName, out var client);
			if (client == null)
			{
				_log.Warning("swap_quant_no_client Worker={Worker} — no llama-rpc client wired (stub)", workerName);
				// Still treat as success: the stub lands with #263, but the
				// Core admission + generation-bump contract is in place.
				return true;
			}
			// Use a sentinel slot key for the engine-wide swap (the swap is
			// per-worker, not per-slot; the slot key just identifies the
			// target worker in the engine's task queue).
			var resp = await client.EngineSwapQuantAsync(workerName, quantKey, tensorPattern, traceId, ct);
			sw.Stop();
			if (resp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			{
				_log.Warning("swap_quant_rpc_error Worker={Worker} Status={Status} DurationMs={Ms}",
					workerName, resp.Status, sw.ElapsedMilliseconds);
				return false;
			}
			_log.Information("swap_quant_ok Worker={Worker} QuantKey={QK} Pattern={P} DurationMs={Ms}",
				workerName, quantKey, tensorPattern, sw.ElapsedMilliseconds);
			return true;
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "swap_quant_exception Worker={Worker}", workerName);
			return false;
		}
		finally
		{
			_tracker.ExitSwapping(workerName);
		}
	}

	private async Task<WorkItemState> ModelLoadAsync(WorkItem item)
	{
		if (_cfg.MixPrecisionEnabled)
		{
			var w = item.State == WorkItemState.ModelLoadPrefill ? item.PrefillWorker! : item.DecodeWorker!;
			var m = item.State == WorkItemState.ModelLoadPrefill ? Router.PrefillModel(w) : Router.DecodeModel(w);
			if (m != null)
			{
			if (_cfg.UseLlamaEngine)
				{
					// Engine mode: the model is loaded at startup via --model
					// flag. Model switching per-request is handled by the
					// "model" field in EnginePrefill (0x42) metadata — no
					// need to call EngineConfigure (0x40) which would
					// interpret the alias as a GGUF file path and crash.
					_log.Information("model_load_skip_engine Mode={M} Worker={W} (handled by EnginePrefill)",
						m, w.Name);
				}
				else
				{
					// Legacy HTTP path (llama-server, not engine)
					var sw = System.Diagnostics.Stopwatch.StartNew();
					var ok = await _proxy.LoadModelAsync(w.LlamaUrl, m, item.TraceId, CancellationToken.None);
					sw.Stop();
					if (ok)
						_log.Information("model_loaded Model={M} Worker={W} DurationMs={Ms}", m, w.Name, sw.ElapsedMilliseconds);
					else
					{
						_log.Warning("model_load_failed Model={M} Worker={W} DurationMs={Ms}", m, w.Name, sw.ElapsedMilliseconds);
						CoordinatorMetrics.ModelLoadDuration.WithLabels(m).Observe(sw.Elapsed.TotalSeconds);
						if (item.State == WorkItemState.ModelLoadPrefill && item.PrefillLease != null)
						{
							await item.PrefillLease.DisposeAsync();
							item.PrefillLease = null;
							SignalEvaluator();
						}
						else if (item.DecodeLease != null)
						{
							await item.DecodeLease.DisposeAsync();
							item.DecodeLease = null;
							SignalEvaluator();
						}
						item.DecodeWorker = null;
						item.DecodeSlot = null;
						item.State = WorkItemState.None;
						return WorkItemState.None;
					}
					CoordinatorMetrics.ModelLoadDuration.WithLabels(m).Observe(sw.Elapsed.TotalSeconds);
				}
			}
		}
		return item.State == WorkItemState.ModelLoadPrefill
			? WorkItemState.PrefixRestore
			: WorkItemState.RestoreKv;
	}

	private async Task<WorkItemState> PrefixRestoreAsync(WorkItem item, CancellationToken ct)
	{
		if (!_cfg.PrefixCheckpointEnabled || item.PrefixHash == null || item.PrefillWorker == null)
		{
			return WorkItemState.Prefill;
		}

		try
		{
			var prefixKey = $"prefix/{item.PrefixHash}.kv";
			var storeResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Get,
				prefixKey, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);

			if (storeResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			{
				CoordinatorMetrics.CacheMisses.Inc();
				item.PrefixCacheHit = false;
				_log.Warning("prefix_not_found Sid={Sid} Hash={Hash}", item.SessionId, item.PrefixHash);
				return WorkItemState.Prefill;
			}

			CoordinatorMetrics.CacheHits.Inc();

			// ── Part B: n_past guard — skip restore when the cached prefix KV
			// already covers ≥ 85% of the new request's estimated tokens.
			// Restoring a stale/large prefix wastes the StatePut RPC and
			// pollutes the slot with KV that will be overwritten by the
			// prefill anyway.
			//
			// The raw prefix blob (Store PUT payload) carries no n_past —
			// Store PUT (0x01) stores the raw llama.cpp KV state bytes,
			// which begin with a magic / version header, not a token count.
			// The n_past is stored separately in the Store's `sessions` PG
			// table by PUT_META (0x14) at save time (non-chunked) or
			// PUT_MANIFEST (0x15, chunked) — both write to the same table.
			// GET_MANIFEST (0x33) reads it back; its response payload is
			// JSON `{"n_past":N, "chunks":[…], …}` — populated in both
			// modes. Pre-#245 prefixes (no sessions row) return NotFound
			// and the guard is silently skipped — back-compat preserved.
			int prefixNPast = 0;
			{
				var manifestResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.GetManifest,
					prefixKey, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
				if (manifestResp.Status == (byte)Hydra.Shared.StatusCode.Ok
					&& manifestResp.Payload is { Length: > 0 })
				{
					try
					{
						var manifestDoc = System.Text.Json.JsonDocument.Parse(manifestResp.Payload);
						if (manifestDoc.RootElement.TryGetProperty("n_past", out var np))
							prefixNPast = np.GetInt32();
					}
					catch { /* non-fatal: guard will be skipped */ }
				}
			}
			item.PrefixNPast = prefixNPast;

			if (prefixNPast > 0 && item.EstimatedTokens > 0
				&& prefixNPast >= item.EstimatedTokens * 0.85)
			{
				_log.Warning("prefix_restore_skipped_n_past Sid={Sid} PrefixNPast={Pnp} EstTokens={Est} Hash={Hash}",
					item.SessionId, prefixNPast, item.EstimatedTokens, item.PrefixHash);
				CoordinatorMetrics.PrefixRestoreSkipped.WithLabels("restore_n_past_guard").Inc();
				item.PrefixCacheHit = false;
				return WorkItemState.Prefill;
			}

			var slotId = item.PrefillSlot ?? 0;
			var llamaRpc = GetStateRpcClient(item.PrefillWorker);
			var putResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StatePut,
				slotId.ToString(), storeResp.Payload, item.TraceId, ct);

			// StatePut succeeded → the prefix KV is now installed in the slot.
			// Set the hit flag only here (not on Store hit alone) so a failed
			// StatePut doesn't mislead the dashboard into thinking the prefix
			// was restored when it actually has to re-prefill.
			item.PrefixCacheHit = true;

			_prefixSet.Add($"{item.PrefillWorker.Name}:{item.PrefixHash}");

			if (putResp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(putResp.Meta);
				var nPast = meta?.TryGetValue("n_past", out var n) == true
					? n.GetInt32() : 0;
				if (nPast > 0)
					_ledger.UpdateNPast(item.SessionId, nPast);
			}

			_log.Information("prefix_restored Sid={Sid} Hash={Hash}",
				item.SessionId, item.PrefixHash);
		}
		catch (Exception ex)
		{
			// StatePut threw — the prefix was found in Store but never
			// installed in the slot. Treat as a miss for the dashboard
			// signal so callers don't see a misleading `prefix_hit=true`.
			item.PrefixCacheHit = false;
			_log.Warning(ex, "prefix_restore_failed");
		}

		return WorkItemState.Prefill;
	}

	// ── Gap 4: n_past tracking in prefill ──

	// ── #592: worker-health recovery on positive liveness evidence ─────────
	//
	// A successful PREFILL is proof the node is alive and serving: the engine
	// tokenized the prompt, built KV and returned a result. During a long inline
	// model swap (T3 rebuild) the health monitor can flag the worker unhealthy
	// (3× health_poll_failed while the engine is busy reloading) even though the
	// engine recovers before the request completes. Clear the stale flag on the
	// PREFILL-success path BEFORE the decode-handoff routing decision so the
	// request isn't 503'd by a flag that no longer reflects reality.
	private void RecoverWorkerHealthByPrefill(WorkItem item, WorkerConfig w)
	{
		if (_health.IsHealthy(w.Name)) return;
		_health.MarkHealthy(w.Name);
		_log.Information("worker_health_recovered_by_prefill Sid={Sid} Node={Node} — stale unhealthy flag cleared by successful PREFILL",
			item.SessionId, w.Name);
	}

	/// <summary>
	/// Release every lease a prefill phase may hold before a failure/retry
	/// re-routes the item. PrefillLease covers the cold_concurrency / PD-split
	/// routes; the cold_atomic route holds the prefill slot via item.DecodeLease
	/// (PrefillLease is deliberately null there — ColdRouteAsync). #635 fix 3:
	/// a prefill failure MUST release BOTH, or the tracker keeps the slot busy
	/// and a re-enqueued/retried item is gated on IsFree forever (observed:
	/// pipeline_retry Retries=2 → queued while the engine was free).
	/// </summary>
	private async Task ReleasePrefillSlotAsync(WorkItem item)
	{
		if (item.PrefillLease != null)
		{
			await item.PrefillLease.DisposeAsync();
			item.PrefillLease = null;
		}
		if (item.DecodeLease != null)
		{
			await item.DecodeLease.DisposeAsync();
			item.DecodeLease = null;
		}
	}

	// #592 router fallback liveness probe: a worker that is free + routable but
	// flagged unhealthy (stale flag from the poll cycle) gets ONE bounded direct
	// probe (GET /health, ≤5s) before being excluded. On success the flag is
	// cleared so the routing picks below see fresh health. No-op when nothing is
	// stale-unhealthy (zero probes → zero latency in the common case).
	//
	// #597: probes run in parallel (Task.WhenAll) so total added latency is
	// bounded at ~5s regardless of how many workers are stale, and concurrent
	// cold requests coalesce onto one shared probe per worker via _probeInFlight
	// instead of each firing their own GET /health. The probe lifetime is tied
	// to the scheduler-wide _cts, NOT to any caller's token: a cancellable
	// caller must never abort the probe for every coalesced waiter.
	private async Task ProbeStaleUnhealthyWorkersAsync()
	{
		var stale = _cfg.Workers.Where(w =>
			w.CanPrefill && _tracker.IsFree(w.Name) && !_health.IsHealthy(w.Name)).ToList();
		if (stale.Count == 0) return;

		// One shared, bounded probe per stale worker: the first caller starts
		// it, concurrent callers observe the same task (Lazy guarantees the
		// probe factory runs at most once per in-flight window).
		var probes = stale.Select(GetOrStartWorkerProbe).ToArray();
		await Task.WhenAll(probes);
	}

	private Task GetOrStartWorkerProbe(WorkerConfig w)
	{
		var probe = _probeInFlight.GetOrAdd(w.Name,
			_ => new Lazy<Task>(() => RunWorkerProbeAsync(w),
				LazyThreadSafetyMode.ExecutionAndPublication));
		return probe.Value;
	}

	private async Task RunWorkerProbeAsync(WorkerConfig w)
	{
		try
		{
			using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
			probeCts.CancelAfter(LivenessProbeTimeout);
			if (await GetLlamaClient(w).HealthAsync(probeCts.Token))
			{
				_log.Information("worker_health_recovered_by_probe Node={Node} — stale unhealthy flag cleared after direct liveness probe", w.Name);
				_health.MarkHealthy(w.Name);
			}
			else
			{
				_log.Warning("worker_health_probe_failed Node={Node} — liveness probe negative, staying unhealthy", w.Name);
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "worker_health_probe_error Node={Node} — liveness probe inconclusive, staying unhealthy", w.Name);
		}
		finally
		{
			// Probe done: evict the entry so the next stale-unhealthy window
			// re-probes fresh health. Callers that already grabbed the task
			// still await the completed result — removing never aborts it.
			_probeInFlight.TryRemove(w.Name, out _);
		}
	}

	private async Task<WorkItemState> PrefillAsync(WorkItem item, CancellationToken ct)
	{
		// Fast path: if the client disconnected while this item was queued/retrying,
		// abort immediately instead of acquiring a slot for a dead request.
		if (item.IsCancelled)
		{
			_log.Information("prefill_cancelled_early Sid={Sid}", item.SessionId);
			return WorkItemState.Cancelled;
		}

		var w = item.PrefillWorker!;
		var engineFailed = false;
		string? engineFailReason = null;

		if (_cfg.UseLlamaEngine)
		{
			// Engine mode: use EnginePrefill RPC which tokenizes internally.
			// HTTP fallback is ONLY for NotImplemented (old binary, #279) —
			// real errors (BUSY, NotFound, timeout) are propagated so the
			// routing layer sees the failure and can retry on another worker
			// or evict the conflicting slot, instead of silently falling back
			// to a slower HTTP path that masks the root cause.
			try
			{
				item.KvRestoredForDecode = false;
				var slotId = item.PrefillSlot ?? 0;
				var body = new Dictionary<string, object>(item.Request)
				{
					["stream"] = false,
					["n_predict"] = 0,
					["messages"] = item.Messages
				};
			// M-Perf.9 #289 + #479/S3: include the prefill model alias so the
			// engine can swap to it (or fall back to the resident model if the
			// alias is unknown / no preset is configured). When null, the
			// engine uses the current resident model — pre-feature behavior.
			// #470: the canonical identity (resolved ONCE at ingress) drives
			// the wire — the raw routing key never reaches the engine. The
			// body copy is OVERWRITTEN unconditionally: BuildPrefillRequestJson
			// only injects `model` when the key is ABSENT (the caller's value
			// wins), so a JsonElement or raw routing key left in the copied
			// body would ride the 0x42 payload untouched.
			var routingAlias = item.ModelIdentity?.RoutingKey;
			var prefillModel = item.ModelIdentity?.PrefillAlias;
			// null is deliberate: the engine falls back to its resident model.
			body["model"] = prefillModel!;
				if (item.PrefillSlot == null)
					item.PrefillSlot = slotId;
				var requestJson = JsonSerializer.Serialize(body);
				var llamaRpc = GetLlamaRpcClient(w);
				// #469 trace: log PREFILL request body for cross-flow comparison
				{
					var msgCount = item.Messages?.Count ?? 0;
					var firstMsg = msgCount > 0 && item.Messages![0].TryGetValue("content", out var fc) ? (fc?.ToString() ?? "?")[..Math.Min(80, (fc?.ToString() ?? "").Length)] : "?";
					var lastMsg = msgCount > 0 && item.Messages![^1].TryGetValue("content", out var lc) ? (lc?.ToString() ?? "?")[..Math.Min(80, (lc?.ToString() ?? "").Length)] : "?";
					_log.Debug("#PD-TRACE PREFILL_REQUEST Sid={Sid} MsgCount={Count} FirstMsg={First} LastMsg={Last} Model={Model} Slot={Slot}",
						item.SessionId, msgCount, firstMsg, lastMsg, prefillModel ?? "(resident)", item.PrefillSlot);
				}
				// Record the elapsed time at the first prefill attempt (once per item).
				if (item.PrefillFirstAttemptMs == 0)
					item.PrefillFirstAttemptMs = item.ElapsedMs;
				var engine = new HydraEngineClient(llamaRpc);
				// Phase 2b (#481): for COMBINED prefills, build hydraConfig
				// from the EngineConfig via TranslateToWirePayloadAsync and
				// merge it into the request body under the hydra_config key.
				// SOLO/ATOMIC prefills pass null (no config injection).
				var hydraConfig = TranslateToWirePayloadAsync(item);
				EnginePrefillResult? prefillResult;
				if (_cfg.EnableChunks)
				{
					if (IsRelayEligible(item, w))
					{
						// #470 Increment 2: direct 0x42→0x43 KV relay. The PREFILL
						// response stream (v2_hdr + state + logits) is drained in the
						// background into a bounded channel consumed by the DECODE
						// RPC — no Store round trip on the hot path (2-4 s saved at
						// 2 GB). The item proceeds to PickDecode while the stream is
						// still flowing; channel backpressure paces the engine send
						// to the decode leg. onMeta captures n_past / kv hash /
						// model identity the moment they arrive (before any KV byte),
						// so the DECODE frame needs no buffering.
						// Persistence: relay turns ride the existing post-decode
						// BgSaveAsync; the store tee is a documented follow-up.
						item.RelayChannel = Channel.CreateBounded<(byte[], int)>(
							new BoundedChannelOptions(4)
							{
								SingleReader = true,
								SingleWriter = true,
								FullMode = BoundedChannelFullMode.Wait
							});
						item.RelayTask = StartRelayPrefillAsync(
							item, w, slotId, engine, requestJson, prefillModel, hydraConfig, ct);
						return WorkItemState.PickDecode;
					}
					// #470 Phase 2: stream the PREFILL response straight into the
					// Store (PutChunked, chunked pipe) — the KV blob (2.3 GB today,
					// 10 GB target) is never materialized in coordinator RAM. The
					// store dedups per chunk and writes the manifest; model identity
					// is stamped afterwards via PutManifestAsync. KvBlob stays null.
					prefillResult = await EnginePrefillChunkedAndStoreAsync(
						item, w, slotId, engine, requestJson, prefillModel, hydraConfig, ct);
				}
				else
				{
					prefillResult = await engine.EnginePrefillAsync(slotId, prefillModel, requestJson, item.TraceId, ct,
						hydraConfig: hydraConfig);
				}
				// Mark whether hydra_config was delivered and the PREFILL succeeded.
				// ApplyMultiEngineAsync checks this to (a) skip a redundant empty-body
				// PREFILL that risks invalidating the KV cache, and (b) record the
				// appropriate telemetry (success or fallback) even when the RPC is skipped.
				// TODO(#487): this only proves the PREFILL RPC transport succeeded, not
				// that the engine actually applied hydra_config correctly — a bad
				// split_mode/tensor_split could fail config-apply while the RPC still
				// returns a non-error status. Once the fork's PREFILL response carries a
				// distinct "hydra_config applied" signal, check it here instead of only
				// !prefillResult.NotImplemented.
				if (hydraConfig is not null)
					item.HydraConfigDeliveredSucceeded = prefillResult is not null && !prefillResult.NotImplemented && !prefillResult.ModelFallback;

				if (prefillResult != null && prefillResult.NotImplemented)
				{
					// Old binary that doesn't know opcode 0x42 (#279).
					// Fall through to the HTTP path below.
					engineFailed = true;
					engineFailReason = "engine does not implement PREFILL opcode";
					item.KvBlob = null;
					CoordinatorMetrics.EnginePrefillFallbacks
						.WithLabels(w.Name, "not_implemented")
						.Inc();
					_log.Warning(
						"engine_prefill_fell_back_to_http Sid={Sid} Worker={W} Slot={Slot} Reason={Reason}",
						item.SessionId, w.Name, item.PrefillSlot, engineFailReason);
				}
				else if (prefillResult != null && prefillResult.IsError)
				{
					// Terminal engine error — not retryable. Fail the request
					// immediately so the routing layer can retry on another worker
					// or surface the error to the client.
					await ReleasePrefillSlotAsync(item);
					item.Error = new InvalidOperationException(
						$"EnginePrefill returned terminal error on {w.Name} (slot={slotId})");
					// #587: expose WHICH non-Ok status + engine meta so bursts
					// of prefill failures are diagnosable at a glance.
					var statusMeta = prefillResult.StatusMeta ?? "";
					_log.Error("prefill_engine_terminal_error Sid={Sid} Worker={W} Slot={Slot} Status={Status} StatusMeta={Meta}",
						item.SessionId, w.Name, slotId, prefillResult.StatusByte,
						statusMeta[..Math.Min(200, statusMeta.Length)]);
					return WorkItemState.Failed;
				}
				else if (prefillResult == null)
				{
					// Slot is busy (BUSY) — release the current slot lease and
					// re-enqueue. The evaluator loop (woken by SignalEvaluator()
					// when any GPU releases a slot) will re-dispatch this item
					// when a slot becomes available.
					await ReleasePrefillSlotAsync(item);
					item.RetryCount++;

					if (item.RetryCount >= WorkItem.MaxRetries)
					{
						_log.Error("prefill_engine_busy_exhausted Sid={Sid} Worker={W} Slot={Slot} Retries={R}",
							item.SessionId, w.Name, slotId, item.RetryCount);
						item.Error = new InvalidOperationException(
							$"EnginePrefill RPC returned BUSY for {item.RetryCount} retries on {w.Name} (slot={slotId})");
						return WorkItemState.Failed;
					}

				// Progress-aware guard: get slot progress info and apply
				// smarter timeout logic based on progress.
				//
				// KNOWN LIMITATION: server_queue is single-threaded — the META
				// query goes through the same queue as the running PREFILL/DECODE
				// task. During real busy periods the META request will queue behind
				// the blocking generation call and time out (5s default) before
				// being serviced. This means progressMeta will be null for most
				// genuinely busy slots, and the code degrades to the workload-aware
				// EstimatedTokens-based timeout (which is still better than the
				// old flat 60s). Fix requires an architecture change in the fork:
				// e.g. an atomic/lock-free progress counter readable without going
				// through server_queue. See https://github.com/ddvnguyen/hydra_vortex/issues/451#issuecomment-XXXXX
				//
				// When progressMeta IS available (rare, or during slot idle
				// windows between tasks), it provides real-time insight.
				// NOTE: PrefillFirstAttemptMs records the timestamp of the first
				// prefill attempt, so busyMs measures actual stuck-in-BUSY time
				// (not total time-in-system which includes queue-wait).
				var busyMs = item.ElapsedMs - item.PrefillFirstAttemptMs;
				SlotMeta? progressMeta = null;
				try
				{
					progressMeta = await GetLlamaClient(w).GetStateMetaAsync(slotId, ct);
				}
				catch (Exception ex)
				{
					_log.Warning(ex, "prefill_slot_busy_progress_query_failed Sid={Sid} Worker={W} Slot={Slot}",
						item.SessionId, w.Name, slotId);
				}

				// Update progress tracking
				if (progressMeta != null && progressMeta.Progress > 0)
				{
					item.LastBusyProgress = progressMeta.Progress;
				}

					// Log progress info with the busy warning
					var progressPct = progressMeta != null ? $"{progressMeta.Progress:P0}" : "unknown";
					var operation = progressMeta?.Operation ?? "unknown";
					var tokensProcessed = progressMeta?.TokensProcessed ?? 0;
					var tokensTotal = progressMeta?.TokensTotal ?? 0;
					var elapsedMs = progressMeta?.ElapsedMs ?? 0;

					_log.Warning("prefill_slot_busy Sid={Sid} Worker={W} Retry={R} BusyMs={BusyMs} TotalMs={TotalMs} Operation={Op} Progress={Progress} TokensProcessed={Tp}/{Tt} ElapsedMs={Em} — re-enqueuing",
						item.SessionId, w.Name, item.RetryCount, busyMs, item.ElapsedMs,
						operation, progressPct, tokensProcessed, tokensTotal, elapsedMs);

					// Workload-aware timeout logic:
					// Calculate expected timeout based on estimated tokens and hardware capabilities.
					// See CalculateBusyTimeouts for formula and testability.
					// #507: detect model swap (T3 rebuild) and add documented load-time headroom.
					// A COMBINED model reload can take minutes — well beyond the token-based
					// timeout. The 6x safety multiplier accounts for the observed 270s vs
					// documented 45s discrepancy in COMBINED reload times.
					int modelLoadTimeS = 0;
					if (prefillModel != null && routingAlias != null)
					{
						var nodeInfo = _health.GetNodeInfo(w.Name);
						var loader = ModelConfigLoader.InstanceOrNull;
						if (loader is not null)
						{
							var template = loader.GetModelTemplate(routingAlias);
							if (template is not null)
							{
						// Check if the requested model's aliases are in the worker's preset.
						// Only apply reload headroom when we have POSITIVE evidence that a
						// swap is needed: nodeInfo is non-null, PresetAliases is populated,
						// and the alias truly isn't in the set. When nodeInfo is null
						// (before first health poll) or PresetAliases is empty (pre-#289
						// engine), we conservatively skip the multiplier to avoid failing
						// open on ordinary contention. #511.
						bool hasPresetData = nodeInfo != null && nodeInfo.PresetAliases.Count > 0;
						bool aliasInPreset = hasPresetData
							&& ((template.PrefillAlias != null && nodeInfo!.PresetAliases.Contains(template.PrefillAlias))
								|| (template.DecodeAlias != null && nodeInfo!.PresetAliases.Contains(template.DecodeAlias)));
						if (hasPresetData && !aliasInPreset)
						{
							modelLoadTimeS = template.LoadTimeS;
							CoordinatorMetrics.ModelReloadTimeoutHeadroom.WithLabels(w.Name, routingAlias).Inc();
						}
					}
				}
			}
				var (stuckTimeoutMs, slowTimeoutMs) = BusyTimeoutOverride?.Invoke(item.EstimatedTokens, modelLoadTimeS)
				?? CalculateBusyTimeouts(item.EstimatedTokens, modelLoadTimeS);

					if (busyMs > stuckTimeoutMs && item.LastBusyProgress == 0)
					{
						// No progress detected for expected time — likely stuck
						item.Error = new InvalidOperationException(
							$"EnginePrefill RPC returned BUSY for {busyMs}ms on {w.Name} " +
							$"(slot={slotId}, retries={item.RetryCount}, operation={operation}). " +
							$"No progress detected (stuck timeout: {stuckTimeoutMs}ms). " +
							$"Check: (1) llama-engine binary supports opcode 0x42, " +
							$"(2) slot count matches HydraCore config, " +
							$"(3) no other process holds all slots.");
						_log.Error("prefill_engine_busy_stuck Sid={Sid} Worker={W} Slot={Slot} Retries={R} BusyMs={Busy} StuckMs={StuckMs} Operation={Op} Progress={Progress} TotalElapsedMs={Ms}",
							item.SessionId, w.Name, slotId, item.RetryCount, busyMs, stuckTimeoutMs, operation, progressPct, item.ElapsedMs);
						return WorkItemState.Failed;
					}
					else if (busyMs > slowTimeoutMs)
					{
						// Has progress but exceeded expected time
						item.Error = new InvalidOperationException(
							$"EnginePrefill RPC returned BUSY for {busyMs}ms on {w.Name} " +
							$"(slot={slotId}, retries={item.RetryCount}, operation={operation}). " +
							$"Progress detected ({progressPct}) but exceeded slow timeout ({slowTimeoutMs}ms). " +
							$"Check: (1) llama-engine binary supports opcode 0x42, " +
							$"(2) slot count matches HydraCore config, " +
							$"(3) no other process holds all slots.");
						_log.Error("prefill_engine_busy_too_slow Sid={Sid} Worker={W} Slot={Slot} Retries={R} BusyMs={Busy} SlowMs={SlowMs} Operation={Op} Progress={Progress} TotalElapsedMs={Ms}",
							item.SessionId, w.Name, slotId, item.RetryCount, busyMs, slowTimeoutMs, operation, progressPct, item.ElapsedMs);
						return WorkItemState.Failed;
					}

				// Re-enqueue — the evaluator will re-dispatch when
				// SignalEvaluator() fires on slot release.
				item.PrefillWorker = null;
				item.PrefillSlot = null;
				item.LastBusyProgress = 0;
				item.State = WorkItemState.None;
				return WorkItemState.Retry;
				}
				else
				{
				// Success — use the engine prefill result.
				item.NPastAfter = prefillResult.NPast;
				item.KvBlob = prefillResult.KvBlob;

				item.KvModelAlias    = prefillResult.ModelAlias;
				item.KvTokenizer     = prefillResult.Tokenizer;
				item.KvModelName     = prefillResult.ModelName;
				item.KvModelQuant    = prefillResult.ModelQuant;
				item.KvModelCapabilities = prefillResult.ModelCapabilities;
				item.KvModelPath     = prefillResult.ModelPath;
				item.KvModelFallback = prefillResult.ModelFallback;
				// #470/A7: stamp the GGUF identity onto the HealthMonitor node
				// so Gate A at DECODE time can compare kv_metadata (what built
				// the KV) against model_metadata (what the decode node should
				// be running) from a genuinely independent source. CurrentModel
				// (the resident alias) feeds the request_timeline model fields.
				_health.UpdateNodeModelIdentity(w.Name, item.KvModelAlias ?? "",
					item.KvTokenizer, item.KvModelName, item.KvModelQuant, item.KvModelCapabilities);
				LastDispatchedModel     = item.KvModelAlias;
				LastDispatchedTokenizer = item.KvTokenizer;
				LastDispatchedModelName = item.KvModelName;
				LastDispatchedModelQuant = item.KvModelQuant;
				LastDispatchedModelCapabilities = item.KvModelCapabilities;
				if (item.KvModelFallback && prefillModel != null)
				{
					CoordinatorMetrics.ModelFallbackTotal
						.WithLabels(w.Name, prefillModel).Inc();
					// Phase 2b (#481): when the engine returns model_fallback=true
					// for a COMBINED prefill, the head did NOT honour the
					// hydra_config.model_path we sent — it served the resident
					// model instead. That's only acceptable if the fork-side
					// 0x42 hydra_config support is not yet landed. Emit a warning
					// so we notice the moment the fork IS ready and we're still
					// silently falling back.
					if (item.MultiMode == MultiEngineMode.Combined)
					{
						_log.Warning("multiengine_model_fallback Sid={Sid} Node={Node} RequestedModel={Req} ResidentModel={Res} Mode={Mode}",
							item.SessionId, w.Name, prefillModel, item.KvModelAlias ?? "(null)", item.MultiMode);
					}
				}

				// #469 trace: log PREFILL response for cross-flow comparison
				_log.Debug("#PD-TRACE PREFILL_RESPONSE Sid={Sid} Node={Node} Slot={Slot} NPast={N} KvBlobSize={BlobSize} Model={Model} Fallback={Fb}",
					item.SessionId, w.Name, slotId, item.NPastAfter, item.KvBlob?.Length ?? 0, item.KvModelAlias ?? "?", item.KvModelFallback);

				// #451: store engine-reported timing metrics into Phases for timeline
				if (prefillResult.PrefillMs > 0)
					item.Phases["prefill_ms"] = (long)prefillResult.PrefillMs;
				if (prefillResult.ModelLoadMs > 0)
				{
					item.Phases["model_load_ms"] = (long)prefillResult.ModelLoadMs;
					CoordinatorMetrics.ModelLoadDuration
						.WithLabels(prefillModel ?? "unknown")
						.Observe(prefillResult.ModelLoadMs / 1000.0);
				}
				if (prefillResult.TokensPerSecond > 0)
					item.Phases["tokens_per_second"] = (long)prefillResult.TokensPerSecond;

				_log.Information("prefill_done Sid={Sid} Node={Node} Slot={Slot} NPastFromEngine={N} EstTokens={Est} Model={Model} Fallback={Fb} PrefillMs={PfMs} ModelLoadMs={MlMs} TokPerSec={Tps}",
					item.SessionId, w.Name, slotId, item.NPastAfter, item.EstimatedTokens,
					item.KvModelAlias ?? "?", item.KvModelFallback,
					prefillResult.PrefillMs, prefillResult.ModelLoadMs, prefillResult.TokensPerSecond);
				// #592: the engine just served this PREFILL — it's demonstrably
				// alive. Clear a stale unhealthy flag BEFORE the decode-handoff
				// routing decision so a poll-cycle failure during an inline
				// model swap can't 503 this request.
				RecoverWorkerHealthByPrefill(item, w);

				// #507: warn when observed model reload time significantly exceeds documented LoadTimeS.
				// Fires on BUSY-retry success (RetryCount > 0) with engine-reported model load time.
				if (item.RetryCount > 0 && prefillResult.ModelLoadMs > 0 && prefillModel != null && routingAlias != null)
				{
					var loader = ModelConfigLoader.InstanceOrNull;
					var template = loader?.GetModelTemplate(routingAlias);
					var documentedLoadMs = (template?.LoadTimeS ?? 0) * 1000L;
					if (documentedLoadMs > 0 && prefillResult.ModelLoadMs > documentedLoadMs * 2)
					{
						_log.Warning("model_reload_exceeds_documented Sid={Sid} Node={Node} Model={Model} " +
							"ObservedMs={Obs} DocumentedMs={Doc} Ratio={Ratio:F1}x",
							item.SessionId, w.Name, prefillModel,
							(long)prefillResult.ModelLoadMs, documentedLoadMs,
							prefillResult.ModelLoadMs / documentedLoadMs);
						CoordinatorMetrics.ModelReloadExceededDocumented
							.WithLabels(w.Name, routingAlias!)
							.Observe(prefillResult.ModelLoadMs / 1000.0);
					}
				}
					if (item.NPastAfter > 0)
					{
						_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
						if (item.PrefillSlot == null || item.PrefillSlot == 0)
							ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
					}
				}
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				// #635 fix 2: a connection-refused on the worker's RPC port means
				// the ENGINE process is down/restarting (the RpcClient already
				// reconnected internally 3× before surfacing). Use the longer
				// backoff so the retry budget covers the restart window.
				var engineRestarting = IsEngineConnectionRefused(ex);
				if (item.RetryCount >= WorkItem.MaxRetries)
				{
					_log.Error(ex,
						"prefill_rpc_error_exhausted Sid={Sid} Worker={W} Slot={Slot} Retries={R}",
						item.SessionId, w.Name, item.PrefillSlot, item.RetryCount);
					await ReleasePrefillSlotAsync(item);
					item.Error = ex;
					return WorkItemState.Failed;
				}
				_log.Warning(ex,
					"prefill_rpc_error Sid={Sid} Worker={W} Slot={Slot} Retry={R}/{Max} — enqueuing retry",
					item.SessionId, w.Name, item.PrefillSlot, item.RetryCount, WorkItem.MaxRetries);
				await ReleasePrefillSlotAsync(item);
				item.PrefillWorker = null;
				item.PrefillSlot = null;
				item.LastBusyProgress = 0;
				item.State = WorkItemState.None;
				item.RetryCount++;
				// #635 fix 2: back off before re-enqueueing — 3 retries at
				// 500ms/2s/8s cover ~10.5s (or ~21s when the engine is
				// restarting) instead of burning the whole budget in ~4s.
				// Deliberately awaited on the pipeline task (holding the
				// evaluator semaphore slot): the item is retrying the SAME
				// worker, and this is what makes the wait-for-restart real.
				var backoff = RetryBackoffOverride?.Invoke(item.RetryCount, engineRestarting)
					?? PrefillRetryBackoff(item.RetryCount, engineRestarting);
				if (backoff > TimeSpan.Zero)
					await Task.Delay(backoff, ct);
				return WorkItemState.Retry;
			}
		}

		// HTTP path — taken when:
		//   - Legacy non-engine mode (_cfg.UseLlamaEngine = false), OR
		//   - Engine mode but the engine returned NotImplemented (old binary, #279).
		// Real engine errors (BUSY, NotFound) are NOT handled here — they throw
		// above so the routing layer can retry/evict instead of masking the issue.
		if (!_cfg.UseLlamaEngine || engineFailed)
		{
			var body = new Dictionary<string, object>(item.Request)
			{
				["stream"] = false,
				["n_predict"] = 0
			};
			// M-Perf.9 #289: pass the configured model alias so llama-server's
			// router mode loads the right preset (when one is configured). In
			// single-model mode this is ignored. We use the configured alias
			// for the model-identity record; the model_hash is filled in by
			// the slot META query below when the server populates it.
			var prefillModel = Router.PrefillModel(w);
			if (prefillModel != null && !body.ContainsKey("model"))
				body["model"] = prefillModel;
			if (item.PrefillSlot == null)
				item.PrefillSlot = await Router.PickIdleSlot(w.LlamaUrl, ct) ?? 0;
			body["id_slot"] = item.PrefillSlot.Value;
			var resp = await _proxy.ProxyCompletionAsync(w.LlamaUrl, body, item.TraceId, ct);
			if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se)
				item.PrefillSlot = se.GetInt32();
			item.LastIdSlot = item.PrefillSlot;

			item.NPastAfter = ExtractTotalTokens(resp);

			// M-Perf.9 #289: capture model identity for the cross-model guard.
			// The HTTP path can't learn the model identity from the response
			// (the OAI completion response doesn't carry it), so we query the
			// slot META. The META call also confirms the slot's n_past and
			// surfaces the engine's model_alias/tokenizer/model_name/model_quant/
			// model_capabilities/model_path fields (when the server supports them).
			item.KvModelAlias = prefillModel;
			try
			{
				var slotMeta = await GetLlamaClient(w).GetStateMetaAsync(item.PrefillSlot ?? 0, ct);
				if (!string.IsNullOrEmpty(slotMeta.ModelAlias))
					item.KvModelAlias = slotMeta.ModelAlias;
				if (!string.IsNullOrEmpty(slotMeta.Tokenizer))
					item.KvTokenizer = slotMeta.Tokenizer;
				if (!string.IsNullOrEmpty(slotMeta.ModelName))
					item.KvModelName = slotMeta.ModelName;
				if (!string.IsNullOrEmpty(slotMeta.ModelQuant))
					item.KvModelQuant = slotMeta.ModelQuant;
				if (slotMeta.ModelCapabilities != 0)
					item.KvModelCapabilities = slotMeta.ModelCapabilities;
				if (!string.IsNullOrEmpty(slotMeta.ModelPath))
					item.KvModelPath = slotMeta.ModelPath;
			}
			catch (Exception ex)
			{
				// Non-fatal: the cross-model guard will skip the check
				// (both identities empty) if we couldn't query META. Logged at
				// Warning for parity with cross_model_check_failed (P2.10
				// consistency) — META failures are a real signal in Loki
				// (operator can spot pre-#470 binaries or transient issues).
				_log.Warning(ex, "prefill_meta_query_failed Slot={Slot}", item.PrefillSlot);
			}
			LastDispatchedModel     = item.KvModelAlias;
			LastDispatchedTokenizer = item.KvTokenizer;
			LastDispatchedModelName = item.KvModelName;
			LastDispatchedModelQuant = item.KvModelQuant;
			LastDispatchedModelCapabilities = item.KvModelCapabilities;

			_log.Information("prefill_done Sid={Sid} Node={Node} Slot={Slot} NPastFromLLama={N} EstTokens={Est} ViaHttp={Http} Model={Model}",
				item.SessionId, w.Name, item.PrefillSlot, item.NPastAfter, item.EstimatedTokens, engineFailed,
				item.KvModelAlias ?? "?");
			// #592: same liveness evidence on the HTTP fallback path — the node
			// just served a PREFILL, so a stale poll-cycle unhealthy flag must
			// not exclude it from the decode-handoff routing decision.
			RecoverWorkerHealthByPrefill(item, w);
			if (item.NPastAfter > 0)
			{
				_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
				if (item.PrefillSlot == null || item.PrefillSlot == 0)
					ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
			}
		}

		CoordinatorMetrics.PrefillDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("prefill_ms") / 1000.0);

		// COMBINED mode: skip KV save — decode happens on the same engine.
		if (item.RequestType == RequestType.Combined)
		{
			item.DecodeWorker = item.PrefillWorker;
			item.DecodeSlot = item.PrefillSlot;
			item.DecodeLease = item.PrefillLease;
			item.PrefillLease = null;
			item.RouteType = "combined";
			_log.Information("combined_prefill_done Sid={Sid} Node={Node} Slot={Slot}",
				item.SessionId, item.DecodeWorker.Name, item.DecodeSlot);
			return WorkItemState.Decode;
		}
		return WorkItemState.SaveKv;
	}

	/// <summary>
	/// #479/S3: translate a Hydra routing identity (<c>moe-35b-pd</c>,
	/// <c>dense-27b-combined</c>, …) to the GGUF-file alias the engine's
	/// <c>--models-preset</c> expects, so the inline PREFILL/decode reload
	/// fires. Role-aware (prefill vs decode quant for P/D split). Returns the
	/// routing identity unchanged when no model template is registered (no
	/// models.json, or legacy alias), preserving pre-feature behavior.
	/// #481 Phase 2c: returns the GGUF-file alias (e.g. <c>qwen3.6-35B-mini</c>),
	/// not the file name — the alias is what the engine's preset maps to a
	/// per-host path. The file name is internal to Core and never crosses the
	/// wire.
	/// </summary>
	private static string? TranslateModelAlias(string? routingAlias, bool decodeRole = false)
	{
		if (string.IsNullOrWhiteSpace(routingAlias))
			return routingAlias;
		var loader = ModelConfigLoader.InstanceOrNull;
		if (loader is null)
			return routingAlias;
		var template = loader.GetModelTemplate(routingAlias);
		if (template is null)
			return routingAlias;
		var alias = decodeRole && !string.IsNullOrWhiteSpace(template.DecodeAlias)
			? template.DecodeAlias
			: template.PrefillAlias;
		return alias ?? routingAlias;
	}

	/// <summary>
	/// #470: read the request's "model" field as a string regardless of the
	/// runtime storage type. The HTTP body is deserialized with default options
	/// (CoordinatorControllers.cs) so values are <see cref="System.Text.Json.JsonElement"/>
	/// — AutoRouter success overwrites "model" with a C# string (line 277) and sets
	/// ForceMode, but when AutoRouter FAILS the field remains a JsonElement while
	/// ForceMode stays empty, which is exactly when the migration/cold paths run.
	/// Unwrap both shapes so the "combined" marker is never silently lost.
	/// </summary>
	private static string? RequestModelString(WorkItem item)
	{
		if (!item.Request.TryGetValue("model", out var mv))
			return null;
		return mv switch
		{
			string s => s,
			System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je
				when je.GetString() is { } js => js,
			_ => null,
		};
	}

	/// <summary>
	/// #589/#609: resolve the alias sent in the merged-decode RPC header
	/// (<c>model</c> — consumed by the engine's DECODE Gate-A name fallback
	/// and the DECODE_APPLY model-swap lookup). Model-agnostic workers
	/// (e.g. P100, <see cref="WorkerConfig.ModelAlias"/> is null) fall back to
	/// <see cref="WorkItem.KvModelAlias"/> (the model the KV was actually
	/// built with — stamped at prefill from the engine's reported alias, or
	/// from the KV manifest on restore) and then to the request's routing
	/// identity — the same rule the HTTP-proxy decode path applies
	/// (#479/S3 + #504). For model-agnostic sessions (no <c>model</c> field)
	/// the KV alias is the only identity available; without it the decode
	/// engine's Gate-A fallback has nothing to match → name=0 reject.
	/// Decode role is honoured so a P/D-split routing identity resolves to
	/// its decode quant (moe-35b-pd → qwen3.6-35B-balanced).
	/// <see cref="TranslateModelAlias"/> is idempotent for already-resolved
	/// aliases (model templates are keyed by routing identities only), so the
	/// KV alias passes through unchanged when the engine already reported the
	/// GGUF alias.
	///
	/// #470 Fix 2: <paramref name="residentMetaAlias"/> — the decode slot's
	/// STATE_META <c>model_alias</c> (what the engine is actually running,
	/// the same source that builds the frame's model_metadata) — takes TOP
	/// precedence. Deriving the decode <c>model</c> from the resident alias
	/// guarantees the frame's <c>model</c> and <c>model_metadata</c> always
	/// describe the same model; sending an alias that contradicts the
	/// metadata (e.g. a KV alias the node no longer hosts) makes the engine
	/// swap on the alias while Gate A validates against the metadata —
	/// a self-contradictory frame. When META is unavailable (query failed or
	/// no alias reported) the historic chain applies unchanged.
	///
	/// #631: for a MIGRATED continuation (<see cref="WorkItem.RouteType"/> ==
	/// "migration" — the session is non-resident on the decode worker), the
	/// historic chain's <c>KvModelAlias</c> describes the model that built the
	/// KV on the SOURCE node, which may not map through the TARGET's preset
	/// table to the target's RESIDENT path (cross-quant: Mini blob →
	/// Balanced resident). Prefer aliases that describe the TARGET's resident
	/// model — <paramref name="healthResidentAlias"/> (the worker's
	/// engine-reported resident alias stamped on the health monitor from
	/// STATE_META/prefill, same source family as residentMetaAlias) and the
	/// request routing identity's DECODE quant — before the source-model KV
	/// alias, so Gate A's #589 fallback sees an alias that maps to the
	/// target's resident path. Non-migrated sessions keep the historic chain
	/// exactly as before.
	/// </summary>
	internal static string? ResolveMergedDecodeModelAlias(WorkItem item, WorkerConfig w,
		string? residentMetaAlias = null, string? healthResidentAlias = null)
	{
		if (!string.IsNullOrEmpty(residentMetaAlias))
			return TranslateModelAlias(residentMetaAlias, decodeRole: true);

		// #631: migrated continuations — the KV alias is the SOURCE node's
		// model, so it must not preempt target-resident-derived aliases.
		if (item.RouteType == "migration")
		{
			if (!string.IsNullOrEmpty(healthResidentAlias))
				return TranslateModelAlias(healthResidentAlias, decodeRole: true);
			if (!string.IsNullOrEmpty(w.ModelAlias))
				return TranslateModelAlias(w.ModelAlias, decodeRole: true);
			// #470: the canonical identity's DECODE alias (already translated
			// at ingress) replaces the raw `is string` request read — the
			// JsonElement shape (AutoRouter failed) previously fell through
			// to the historic chain and could send the source-node quant.
			var migReqModel = item.ModelIdentity?.DecodeAlias ?? RequestModelString(item);
			if (!string.IsNullOrEmpty(migReqModel))
				return TranslateModelAlias(migReqModel, decodeRole: true);
			// Fall through to the historic chain — KvModelAlias remains the
			// last resort (same-model migration: source == target resident,
			// so the KV alias DOES map to the resident path).
		}

		if (!string.IsNullOrEmpty(w.ModelAlias))
			return TranslateModelAlias(w.ModelAlias, decodeRole: true);
		if (!string.IsNullOrEmpty(item.KvModelAlias))
			return TranslateModelAlias(item.KvModelAlias, decodeRole: true);
		// #470: last `is string` fixed — the identity record's DECODE alias
		// is the single source for the request's model; RequestModelString
		// backs legacy/unit items constructed without SubmitAsync. The
		// result stays idempotent through TranslateModelAlias (already-
		// resolved GGUF aliases pass through unchanged).
		var reqModel = item.ModelIdentity?.DecodeAlias ?? RequestModelString(item);
		return TranslateModelAlias(reqModel, decodeRole: true);
	}

	/// <summary>
	/// #470 Phase 2: chunked PREFILL whose response payload is streamed straight
	/// into the Store via PUT_CHUNKED (0x10) — no full-blob byte[] in coordinator
	/// RAM (2.3 GB today, 10 GB target). Chunks are assembled at
	/// <see cref="ChunkEngine.CHUNK_SIZE"/> boundaries from offset 0 (the same
	/// boundaries the Store's chunker uses, so dedup + manifest stay consistent),
	/// hashed for the L1 chunk cache, and written into a Pipe consumed by the
	/// Store RPC (backpressure throttles the engine read naturally). The Store
	/// dedups per chunk and writes the manifest; model identity + n_past are
	/// stamped afterwards via <see cref="PutManifestAsync"/>. On success
	/// <see cref="WorkItem.KvStreamedToStore"/> is set so SaveKvAsync skips the
	/// buffered push; KvBlob stays null.
	/// </summary>
	private async Task<EnginePrefillResult?> EnginePrefillChunkedAndStoreAsync(
		WorkItem item, WorkerConfig w, int slotId, HydraEngineClient engine,
		string requestJson, string? prefillModel, Dictionary<string, object>? hydraConfig,
		CancellationToken ct)
	{
		var storeKey = $"{item.SessionId}.kv";
		var chunkSize = ChunkEngine.CHUNK_SIZE;
		var chunks = new List<ChunkRef>();
		var chunkBuffer = new byte[chunkSize];
		var chunkPos = 0;
		var totalSize = 0L;
		Pipe? pipe = null;
		Task? pushTask = null;
		Exception? pushError = null;

		EnginePrefillResult? result = null;
		try
		{
			result = await engine.EnginePrefillChunkedAsync(
				slotId, prefillModel, requestJson, item.TraceId, ct, hydraConfig,
				onPayloadLen: len =>
				{
					// Fires once, before the first chunk: start the Store push with
					// the exact frame size. A fresh pipe per attempt (no retries
					// inside the chunked read, so this runs at most once).
					totalSize = len;
					pipe = new Pipe(new PipeOptions(
						pauseWriterThreshold: 4 * 1024 * 1024,
						resumeWriterThreshold: 1 * 1024 * 1024));
					pushTask = Task.Run(async () =>
					{
						try
						{
							var resp = await StoreClient.RequestStreamBodyAsync(
								OpCode.PutChunked, storeKey, pipe.Reader.AsStream(),
								totalSize, item.TraceId, ct);
							if (resp.Status != (byte)StatusCode.Ok)
								throw new InvalidDataException(
									$"PUT_CHUNKED failed (status=0x{resp.Status:X2}): {resp.Meta}");
						}
						catch (Exception ex)
						{
							pushError = ex;
							throw;
						}
					});
				},
				onChunk: async (mem, token) =>
				{
					var off = 0;
					while (off < mem.Length)
					{
						var n = Math.Min(chunkSize - chunkPos, mem.Length - off);
						mem.Slice(off, n).CopyTo(chunkBuffer.AsMemory(chunkPos));
						chunkPos += n;
						off += n;
						if (chunkPos == chunkSize)
						{
							var hash = ChunkEngine.ComputeHash(chunkBuffer.AsSpan());
							chunks.Add(new ChunkRef(chunks.Count, hash, chunkSize));
							await SaveChunkToL1BestEffortAsync(
								item.SessionId, hash, chunkBuffer.ToArray(), token);
							await pipe!.Writer.WriteAsync(chunkBuffer.AsMemory(), token);
							chunkPos = 0;
						}
					}
				});
		}
		catch (Exception)
		{
			// Engine stream aborted mid-payload: fault the pipe so the Store push
			// unblocks with the same failure instead of hanging on EOF.
			if (pipe != null)
			{
				await pipe.Writer.CompleteAsync(
					new IOException("engine prefill stream aborted mid-payload"));
				try { if (pushTask != null) await pushTask; } catch { /* pushError already set */ }
			}
			throw;
		}

		// The response was fully consumed (Ok, Busy, Error, ...) — the payload
		// stream ran to completion, so the tail chunk is valid and the pipe can
		// be closed cleanly.
		if (chunkPos > 0)
		{
			var tailBytes = chunkBuffer.AsSpan(0, chunkPos).ToArray();
			var hash = ChunkEngine.ComputeHash(tailBytes.AsSpan());
			chunks.Add(new ChunkRef(chunks.Count, hash, tailBytes.Length));
			await SaveChunkToL1BestEffortAsync(item.SessionId, hash, tailBytes, ct);
			if (pipe != null)
				await pipe.Writer.WriteAsync(tailBytes, ct);
			chunkPos = 0;
		}

		if (pipe != null)
		{
			await pipe.Writer.CompleteAsync(pushError);
			if (pushTask != null)
				await pushTask; // rethrows the Store error when the push failed
		}

		if (result == null || result.IsError || result.NotImplemented)
			return result;

		// Stamp n_past + model identity onto the manifest the Store wrote during
		// PutChunked (it carries chunks + sizes only). Total size == payload len.
		await PutManifestAsync(storeKey, result.NPast, totalSize, chunks,
			item.TraceId, ct,
			result.ModelAlias ?? "", result.Tokenizer ?? "", result.ModelName ?? "",
			result.ModelQuant ?? "", result.ModelCapabilities, result.ModelPath ?? "");
		item.KvBytes = totalSize;
		item.KvStreamedToStore = true;
		item.KvHash = result.KvHash; // engine-computed xxh3 over v2_hdr+state+logits ("" when old binary)
		_log.Information("prefill_streamed_to_store Sid={Sid} Node={Node} Slot={Slot} Bytes={Bytes} Chunks={Chunks} KvHash={Hash}",
			item.SessionId, w.Name, slotId, totalSize, chunks.Count, string.IsNullOrEmpty(item.KvHash) ? "-" : item.KvHash);
		return result;
	}

	/// <summary>
	/// #470 Increment 2 (relay): true when this turn should use the direct
	/// 0x42→0x43 KV relay instead of the Store round trip. Requires a decode
	/// node distinct from the prefill node that advertises merged_decode —
	/// a same-node relay would deadlock (the decode task queues behind the
	/// still-streaming prefill task on the engine's single queue). Same-node
	/// routes (COMBINED, cold_atomic, fallback picks) keep the existing
	/// store/no-restore paths.
	/// </summary>
	private bool IsRelayEligible(WorkItem item, WorkerConfig prefillWorker)
	{
		if (item.RouteType == "cold_atomic")
			return false;
		if (item.MultiMode == MultiEngineMode.Combined)
			return false;
		if (!item.IsStreaming)
			return false;
		var combinedReq = RequestModelString(item) is { } crm
			&& crm.Contains("combined", StringComparison.OrdinalIgnoreCase);
		if (combinedReq)
			return false;
		var dw = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health,
			prefillWorker.Name, allowedModels: _cfg.AllowedModels);
		if (dw == null || dw.Name == prefillWorker.Name)
			return false;
		if (!dw.CanDecode)
			return false;
		return _health.GetNodeInfo(dw.Name)?.EngineCapabilities?.Contains(Protocol.CapMergedDecode) == true;
	}

	/// <summary>
	/// #470 Increment 2 (relay): drains the PREFILL response stream in the
	/// background. Each chunk is copied into a rented array and written to the
	/// bounded relay channel (the RPC read loop reuses its 1 MiB buffer, so a
	/// copy is mandatory); the decode consumer returns the array to the pool.
	/// onMeta captures the decode-frame inputs (n_past, whole-segment xxh3,
	/// kv model identity) before the first KV byte arrives. The channel is
	/// completed (with the failure, if any) when the stream ends; the RTX
	/// prefill slot is released in finally (the stream ends exactly when the
	/// decode leg consumed it all — or the RPC failed).
	/// </summary>
	private async Task<EnginePrefillResult?> StartRelayPrefillAsync(
		WorkItem item, WorkerConfig w, int slotId, HydraEngineClient engine,
		string requestJson, string? prefillModel, Dictionary<string, object>? hydraConfig,
		CancellationToken ct)
	{
		var channel = item.RelayChannel!;
		try
		{
			// Idle budget == ceiling: while the bounded channel is full (decode
			// side preparing — model load, slot acquisition), the read loop is
			// parked inside onChunk and no chunks reset the idle timer; only the
			// total ceiling must bound the RPC.
			var result = await engine.EnginePrefillChunkedAsync(
				slotId, prefillModel, requestJson, item.TraceId, ct, hydraConfig,
				onPayloadLen: len => item.RelayKvTotalSize = len,
				onChunk: async (mem, token) =>
				{
					var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(mem.Length);
					mem.Span.CopyTo(buf);
					await channel.Writer.WriteAsync((buf, mem.Length), token);
				},
				onMeta: meta =>
				{
					item.NPastAfter = meta.NPast;
					item.KvHash = meta.KvHash;
					item.KvBytes = meta.StateSize;
					if (!string.IsNullOrEmpty(meta.ModelAlias)) item.KvModelAlias = meta.ModelAlias;
					if (!string.IsNullOrEmpty(meta.Tokenizer)) item.KvTokenizer = meta.Tokenizer;
					if (!string.IsNullOrEmpty(meta.ModelName)) item.KvModelName = meta.ModelName;
					if (!string.IsNullOrEmpty(meta.ModelQuant)) item.KvModelQuant = meta.ModelQuant;
					if (!string.IsNullOrEmpty(meta.ModelPath)) item.KvModelPath = meta.ModelPath;
					if (meta.ModelCapabilities != 0) item.KvModelCapabilities = meta.ModelCapabilities;
				},
				requestTimeoutOverride: TimeSpan.FromSeconds(600),
				payloadIdleBudget: TimeSpan.FromSeconds(600));
			channel.Writer.TryComplete();
			_log.Information("relay_prefill_done Sid={Sid} Node={Node} Slot={Slot} Bytes={Bytes} NPast={N} KvHash={Hash}",
				item.SessionId, w.Name, slotId, item.RelayKvTotalSize, item.NPastAfter,
				string.IsNullOrEmpty(item.KvHash) ? "-" : item.KvHash);
			return result;
		}
		catch (Exception ex)
		{
			channel.Writer.TryComplete(ex); // unblocks the decode writer with the failure
			throw;
		}
		finally
		{
			// The prefill RPC completed — the stream was fully consumed by the
			// decode leg (or failed) — the RTX slot is free again (mirrors
			// SaveKvAsync's "Release GPU slot immediately").
			if (item.PrefillLease != null)
			{
				await item.PrefillLease.DisposeAsync();
				item.PrefillLease = null;
				SignalEvaluator();
				_log.Information("relay_prefill_slot_released Sid={Sid} Node={Node} Slot={Slot}",
					item.SessionId, w.Name, slotId);
			}
		}
	}

	/// <summary>
	/// #470 Increment 2 (relay): adapter from the bounded relay channel to the
	/// DECODE RPC's kvChunks enumerable. Returns each rented array to
	/// ArrayPool after the consumer finished writing it to the socket.
	/// </summary>
	private static async IAsyncEnumerable<ReadOnlyMemory<byte>> RelayKvChunksAsync(
		Channel<(byte[] Buffer, int Length)> channel,
		[EnumeratorCancellation] CancellationToken ct)
	{
		await foreach (var (buf, len) in channel.Reader.ReadAllAsync(ct))
		{
			try
			{
				yield return buf.AsMemory(0, len);
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(buf);
			}
		}
	}

	/// <summary>
	/// Best-effort L1 chunk-cache save with evict-on-ENOSPC recovery (#470).
	/// The L1 tmpfs cache shares /mnt/llm-ram with the Store's chunk dir, so
	/// a full mount surfaces here as an IOException from the L1 write. The L1
	/// save is a cache optimization, NOT a correctness requirement — an L1
	/// miss falls back to L2/Store via GetChunkDataAsync — so a failed save
	/// must never abort the prefill stream (pre-#470 an ENOSPC here propagated
	/// out of onChunk, left the RPC socket mid-frame, and killed the turn with
	/// prefill_rpc_error_exhausted). On IOException: evict L1 LRU sessions
	/// (frees the shared tmpfs, the same recovery as PushChunkBatchAsync's
	/// #615 fix) and retry once; if the retry still fails, log a warning and
	/// continue without throwing. Cancellation still propagates.
	/// </summary>
	internal async Task SaveChunkToL1BestEffortAsync(
		string sessionId, string hash, byte[] chunkData, CancellationToken ct)
	{
		if (_chunkCache == null) return;
		// #470 Tier-4: track this session in the scheduler-side L1 registry
		// (write-order) so a later ENOSPC can force-evict the OLDEST
		// non-in-flight sessions even when the L1's own byte-LRU is a no-op.
		RegisterL1Session(sessionId);
		try
		{
			await _chunkCache.SaveChunkDataAsync(sessionId, hash, chunkData, ct);
		}
		catch (IOException)
		{
			var evicted = 0;
			try { evicted = await _chunkCache.EvictLRUAsync(); }
			catch { /* eviction is best-effort too: a throwing evict still
					   removes the LRU session from the index before it fails,
					   and the retry save can succeed without it */ }
			// #470 Tier-4: the L1 byte-budget LRU is a no-op when the L1 is
			// UNDER its own cap — but the L1 shares /mnt/llm-ram with the
			// Store's chunk dir, and a full mount is exactly when bytes must
			// be freed for the retry to succeed. When the LRU freed nothing,
			// force-evict the OLDEST non-in-flight sessions tracked by this
			// scheduler (a fallen-back / parked session must not hold its
			// chunks hostage).
			if (evicted == 0)
				evicted += await EvictL1OnEnospcAsync(sessionId);
			try
			{
				await _chunkCache.SaveChunkDataAsync(sessionId, hash, chunkData, ct);
			}
			catch (IOException)
			{
				_log.Warning("chunk_cache_l1_save_failed sid={Sid} hash={Hash} bytes={Bytes} evicted={Evicted}",
					sessionId, hash, chunkData.Length, evicted);
			}
		}
	}

	private async Task<WorkItemState> SaveKvAsync(WorkItem item, CancellationToken ct)
	{
		// HYDRA_COORD_NO_STORE_KV_RESTORE=true: skip saving KV to Store.
		// No point saving what we'll never restore.
		if (_cfg.NoStoreKvRestore)
		{
			_log.Information("save_kv_skipped Sid={Sid} (NoStoreKvRestore=true)", item.SessionId);
			// Release the prefill slot lease — the normal cleanup path
			// (SaveDone → MarkEvicted) is bypassed when returning Decode.
			if (item.PrefillLease != null)
			{
				await item.PrefillLease.DisposeAsync();
				item.PrefillLease = null;
			}
			return WorkItemState.Decode;
		}

		var w = item.PrefillWorker!;
		var slotId = item.PrefillSlot ?? 0;
		_log.Information("save_kv_start Sid={Sid} Slot={Slot} NPast={N} Node={Node}",
			item.SessionId, slotId, item.NPastAfter, w.Name);
		try
		{
			// ── Phase 1: Engine RPC — pull KV blob + model identity ──
			// Only this phase needs the GPU slot.
			byte[]? payload;
			if (_cfg.EnableChunks && item.KvStreamedToStore)
			{
				// #470 Phase 2: the prefill response was already streamed into the
				// Store (chunked pipe) — there is no blob to pull and nothing to
				// push. Release the GPU slot and proceed straight to decode.
				payload = null;
			}
			else if (_cfg.UseLlamaEngine && item.KvBlob != null)
			{
				var rpcMs = item.RecordPhase("save_kv_rpc_ms");
				CoordinatorMetrics.SaveKvRpcDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(rpcMs / 1000.0);
				payload = item.KvBlob;
				item.KvBlob = null;
			}
			else
			{
				payload = await SaveKvStateCoreAsync(w, slotId, item.SessionId, item.NPastAfter, item.TraceId, ct);
				var rpcMs = item.RecordPhase("save_kv_rpc_ms");
				CoordinatorMetrics.SaveKvRpcDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(rpcMs / 1000.0);
			}

			if (payload == null && !item.KvStreamedToStore)
				throw new InvalidOperationException($"StateGet RPC failed for save");

			if (payload != null)
				item.KvBytes = payload.Length;

			// ── Release GPU slot immediately ──────────────────────────
			// KV blob is in Coordinator memory (or the Store) — GPU no longer needed.
			// Store writes below run in parallel but don't need the GPU.
			if (item.PrefillLease != null)
			{
				await item.PrefillLease.DisposeAsync();
				item.PrefillLease = null;
				SignalEvaluator();
				_log.Information("save_kv_gpu_released Sid={Sid} Node={Node} Slot={Slot} — GPU free, Store writes next",
					item.SessionId, w.Name, slotId);
			}

			// ── Phase 2: Store writes (parallel, no GPU needed) ───────
			var storeKey = $"{item.SessionId}.kv";
			if (_cfg.EnableChunks && item.KvStreamedToStore)
			{
				// Already streamed during the prefill response (PutChunked +
				// manifest identity stamp) — nothing to do.
				_log.Debug("save_kv_streamed_already Sid={Sid} Bytes={Bytes}",
					item.SessionId, item.KvBytes);
			}
			else if (_cfg.EnableChunks)
			{
				var chunks = ChunkEngine.ChunkAndHash(payload!);
				var orderedHashes = chunks.Select(c => c.Hash).ToList();
				var missing = await SyncMissingAsync(storeKey, orderedHashes, item.TraceId, ct);
				await PushMissingChunksParallelAsync(storeKey, item.SessionId, missing, chunks, payload, item.TraceId, ct);
				// M-Perf.9 #289 / #470: persist model identity alongside the KV so
				// the cross-model guard in RestoreKvAsync can detect a model
				// swap between prefill and decode (e.g. Mini prefill → Balanced
				// decode would otherwise silently corrupt the response).
				await PutManifestAsync(
					storeKey, item.NPastAfter, payload.Length, chunks,
					item.TraceId, ct,
					item.KvModelAlias ?? "", item.KvTokenizer ?? "", item.KvModelName ?? "",
					item.KvModelQuant ?? "", item.KvModelCapabilities, item.KvModelPath ?? "");
				var storeMs = item.RecordPhase("save_kv_store_ms");
				CoordinatorMetrics.SaveKvStoreDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(storeMs / 1000.0);

				// M-Perf.10: chunk-cache writes are local tmpfs and used
				// only as a future-read optimisation. Make them truly
				// fire-and-forget so they no longer block the request.
				// The C# Store writes above already completed (manifest
				// is the source of truth for restore), so the request
				// can proceed to decode immediately. The chunk-cache
				// will be populated within a few hundred ms in the
				// background; if the next request on the same prefix
				// arrives before the cache write finishes, it falls
				// through to the Store (which has the chunks).
				if (_chunkCache != null)
				{
					var cache = _chunkCache;
					var sid = item.SessionId;
					var payloadCopy = payload;
					var hashesCopy = orderedHashes;
					var chunkSize = _cfg.ChunkSize;
					_ = Task.Run(async () =>
					{
						try
						{
							// #470 Tier-4: track this session in the L1
							// registry so ENOSPC can force-evict it if it is
							// ever the oldest non-in-flight candidate.
							RegisterL1Session(sid);
							await cache.SaveHashesAsync(sid, hashesCopy, CancellationToken.None);
							foreach (var c in chunks)
							{
								var slice = payloadCopy.AsSpan(
									c.Index * chunkSize,
									Math.Min(chunkSize, (int)(payloadCopy.Length - c.Index * chunkSize))).ToArray();
								await cache.SaveChunkDataAsync(sid, c.Hash, slice, CancellationToken.None);
							}
						}
						catch (Exception ex)
						{
							_log.Warning(ex, "chunk_cache_bg_save_failed Sid={Sid}", sid);
						}
					});
				}
			}
			else
			{
				await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
					storeKey, payload, item.TraceId, ct);
				var storeMs = item.RecordPhase("save_kv_store_ms");
				CoordinatorMetrics.SaveKvStoreDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(storeMs / 1000.0);
			}

			var entry = _ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter, item.PrefixHash);
			lock (entry) { entry.HasStoreState = true; }
			item.Entry = entry;
			_log.Information("state_saved Sid={Sid} SizeMB={Size}", item.SessionId, (payload?.Length ?? item.KvBytes) / 1024 / 1024);

			if (item.PrefixHash != null && _cfg.PrefixCheckpointEnabled)
			{
				// ── Part C: prefix-save truncation guard (#245) ──
				// The prefix checkpoint should only contain KV state for the system
				// prompt tokens — not the full request. Saving the full request blob
				// under the system-prompt key is the "live poisoning" bug: every
				// subsequent request with the same system prompt restores a KV blob
				// that is much larger than needed, wasting the StatePut RPC and
				// polluting the slot with stale KV.
				//
				// When SystemPromptTokens is available (> 0), the restore guard in
				// PrefixRestoreAsync will skip the restore if the prefix blob's
				// n_past already covers the new request — so saving a larger blob
				// is wasteful but not catastrophic.
				//
				// When SystemPromptTokens is 0 (cannot determine the system-prompt
				// boundary), saving the full request blob under the system-prompt
				// key IS the live poisoning bug. Skip the save entirely.
				if (item.SystemPromptTokens <= 0)
				{
					_log.Information("prefix_save_skipped_no_boundary Sid={Sid} Hash={Hash} — system-prompt token boundary unknown",
						item.SessionId, item.PrefixHash);
					CoordinatorMetrics.PrefixSavePayloadTruncated.WithLabels("no_system_prompt_boundary").Inc();
				}
				else
				{
					var prefixKey = $"prefix/{item.PrefixHash}.kv";
					var kvPayload = payload;
					var traceId = item.TraceId;
					var sysTokens = item.SystemPromptTokens;
					var prefixNPast = item.NPastAfter;
					_ = Task.Run(async () =>
					{
						try
						{
							var stat = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Stat,
								prefixKey, ReadOnlyMemory<byte>.Empty, traceId, CancellationToken.None);
							if (stat.Status != (byte)Hydra.Shared.StatusCode.Ok)
							{
								// ── #245 fix: persist the prefix's n_past so the
								// restore-time guard in PrefixRestoreAsync can read
								// it back via GET_MANIFEST. The raw blob has no
								// n_past header — Store PUT (0x01) stores the raw
								// llama.cpp KV state bytes. PUT_META (0x14) writes
								// n_past to the Store's `sessions` PG table, which
								// GET_MANIFEST returns. Chunked saves get the same
								// effect from PUT_MANIFEST (0x15) — the two
								// opcodes write to the same table.
								var metaPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
									new { n_past = prefixNPast });
								await StoreClient.RequestAsync(Hydra.Shared.OpCode.PutMeta,
									prefixKey, metaPayload, traceId, CancellationToken.None);

								await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
									prefixKey, kvPayload, traceId, CancellationToken.None);
								CoordinatorMetrics.PrefixSaves.Inc();
								_log.Information("prefix_saved Hash={Hash} SizeMB={Size} SysTokens={Sys} NPast={NPast}",
									item.PrefixHash, kvPayload.Length / 1024 / 1024, sysTokens, prefixNPast);
							}
						}
						catch (Exception ex)
						{
							CoordinatorMetrics.PrefixSaveFailures.Inc();
							_log.Warning(ex, "prefix_save_failed Hash={Hash}", item.PrefixHash);
						}
					});
				}
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "save_failed_fallback Sid={Sid} — falling back to same-node decode", item.SessionId);
			// #470 Tier-4: a save failure leaves this session's chunks pinned in
			// the L1 chunk cache forever (they are never released, evicted=0) and
			// the shared /mnt/llm-ram never drains — the cascade the whole suite
			// hits. Release them on the fallback so the space returns to the pool.
			// The same-node decode below runs off the KV resident in the engine
			// slot, so the L1 (a future-read cache) is not needed for this turn.
			// Best-effort: a clear failure logs and must not block the fallback.
			if (_chunkCache != null)
			{
				try { await _chunkCache.ClearAsync(item.SessionId); }
				catch (Exception clearEx)
				{
					_log.Warning(clearEx, "save_fallback_l1_clear_failed Sid={Sid}", item.SessionId);
				}
			}
			if (item.Entry != null) { lock (item.Entry) { item.Entry.HasStoreState = false; } }
			if (item.DecodeLease != null) { await item.DecodeLease.DisposeAsync(); item.DecodeLease = null; SignalEvaluator(); }
			if (item.PrefillWorker?.CanDecode == true
				&& _tracker.TryAcquireSlot(item.PrefillWorker.Name, out var fbSlot, "decode-fallback"))
			{
				item.DecodeWorker = item.PrefillWorker;
				item.DecodeSlot = fbSlot;
				item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				_log.Information("save_fallback_decode Sid={Sid} Node={Node} Slot={Slot}",
					item.SessionId, item.PrefillWorker.Name, fbSlot);
				CoordinatorMetrics.DecodeFallbackTotal.WithLabels("save_fail").Inc();
				// Even on fallback, record the phases we did observe
				// (e.g. the RPC happened; the store write failed). The
				// catch block runs after the try body, so the rpc +
				// store checkpoint values are already in item.Phases;
				// we record the "save_kv_ms" rollup from them.
				var totalMs = item.Phases.GetValueOrDefault("save_kv_rpc_ms")
					+ item.Phases.GetValueOrDefault("save_kv_store_ms");
				CoordinatorMetrics.SaveKvDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(totalMs / 1000.0);
				return WorkItemState.Decode;
			}
			_log.Error("save_fallback_no_slot Sid={Sid} — prefill node has no free decode slot", item.SessionId);
			return WorkItemState.Failed;
		}
		// Back-compat metric: sum of the two new phases. Existing
		// dashboards / alerts on hydra_save_kv_seconds keep working.
		// Don't call RecordPhase("save_kv_ms") here — that would
		// measure the small post-store overhead (ledger.Register),
		// not the sum we want. Read the parts from Phases instead.
		var total = item.Phases.GetValueOrDefault("save_kv_rpc_ms")
			+ item.Phases.GetValueOrDefault("save_kv_store_ms");
		CoordinatorMetrics.SaveKvDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(total / 1000.0);
		return WorkItemState.SaveDone;
	}

	private async Task<WorkItemState> MarkEvictedStateAsync(WorkItem item)
	{
		_ledger.MarkEvicted(item.SessionId);
		if (item.PrefillLease != null)
		{
			await item.PrefillLease.DisposeAsync();
			item.PrefillLease = null;
		}
		return item.State == WorkItemState.SaveDone
			? WorkItemState.PickDecode
			: WorkItemState.Done;
	}

	private async Task<WorkItemState> PickDecodeAsync(WorkItem item)
	{
		// COMBINED mode safety guard: COMBINED items skip SaveKv entirely and
		// go directly PrefillAsync → DecodeAsync. If a COMBINED item reaches
		// here, it's a bug — log and return Decode on the prefill worker.
		if (item.RequestType == RequestType.Combined && item.PrefillWorker != null)
		{
			_log.Error("combined_pickdecode_unexpected Sid={Sid} Node={Node} — COMBINED item reached PickDecode (should have been skipped by PrefillAsync)",
				item.SessionId, item.PrefillWorker.Name);
			item.DecodeWorker = item.PrefillWorker;
			item.DecodeSlot = item.PrefillSlot;
			item.DecodeLease = item.PrefillLease;
			item.PrefillLease = null;
			return WorkItemState.Decode;
		}

		// COMBINED mode: decode must stay on the head — the peer (rtx3060) is
		// exclusively reserved and the expert-mode split is wired to this head.
		// PickBestDecodeWorker would wander to P100 and break the dual-GPU binding.
		WorkerConfig? dw;
		if (item.MultiMode == MultiEngineMode.Combined && item.PrefillWorker != null)
		{
			dw = item.PrefillWorker;
			_log.Warning("combined_pickdecode Sid={Sid} Node={Node} — decode forced to prefill node for COMBINED mode",
				item.SessionId, dw.Name);
			CoordinatorMetrics.DecodeFallbackTotal.WithLabels("combined_pickdecode").Inc();
		}
		else if (item.RouteType == "cold_atomic" && item.PrefillWorker != null)
		{
			// Atomic = single-GPU intent: the prefill built KV on this worker
			// and the KV is resident there. #470 merged-decode routes cold
			// atomic requests through Prefill first (so Gate A has kv_metadata
			// + a KV blob to validate); decoding must then stay on the SAME
			// worker — PickBestDecodeWorker would otherwise wander to a
			// higher-decode-priority peer (e.g. p100) and force a needless
			// cross-node KV transfer (or a Gate A rejection on an empty slot).
			//
			// NB: item.RequestType is overwritten to Decode by the pipeline
			// SaveDone→PickDecode handoff (RunItemPipeline), so the atomic
			// intent is tracked via RouteType == "cold_atomic" instead.
			//
			// The cold_atomic route already acquired the slot up-front and
			// holds it via item.DecodeLease (PrefillLease is deliberately
			// null — see ColdRouteAsync). Re-acquiring via TryAcquireSlot
			// would fail (the slot is held by our own DecodeLease) and
			// wander to p100. Reuse the existing lease when it targets the
			// prefill worker.
			if (item.DecodeLease?.WorkerName == item.PrefillWorker.Name
				&& item.DecodeLease.SlotId == (item.PrefillSlot ?? 0))
			{
				dw = item.PrefillWorker;
				item.DecodeSlot = item.DecodeLease.SlotId;
				_log.Information("atomic_pickdecode_reuse_lease Sid={Sid} Node={Node} Slot={Slot} — decode reuses cold_atomic lease (atomic single-GPU)",
					item.SessionId, dw.Name, item.DecodeSlot);
			}
			else
			{
				dw = item.PrefillWorker;
				_log.Information("atomic_pickdecode Sid={Sid} Node={Node} — decode stays on prefill node (atomic single-GPU)",
					item.SessionId, dw.Name);
			}
		}
		else
		{
			// #470 chokepoint (investigator 0ef8f152, L1/L3/L5): a COMBINED-model
			// request must NEVER resolve to a non-CombinedCapable decode worker
			// (e.g. p100 — DecodePriority=1, model 35B-Balanced, would decode a
			// 27B-Coder KV → wrong-model → stream_done_no_lease wedge, observed
			// live trace 4717737069544794). PickBestDecodeWorker is priority-only
			// and model-agnostic; gate on the request marker (raw string, handles
			// JsonElement) here — the funnel all leak paths converge on.
			var combinedReq = RequestModelString(item) is { } crm
				&& crm.Contains("combined", StringComparison.OrdinalIgnoreCase);
			if (combinedReq)
			{
				// Combined decode must stay on a CombinedCapable head. Prefer the
				// prefill worker (rtx) when it qualifies; otherwise fall back to
				// another CombinedCapable head; NEVER a plain decode worker.
				if (item.PrefillWorker?.CombinedCapable == true)
				{
					dw = item.PrefillWorker;
					_log.Information("combined_pickdecode_head Sid={Sid} Node={Node} — combined decode pinned to prefill head",
						item.SessionId, dw.Name);
				}
				else
				{
					dw = _cfg.Workers.FirstOrDefault(w => w.CombinedCapable && w.IsHead
						&& _tracker.IsFree(w.Name) && _health.IsHealthy(w.Name));
					if (dw != null)
						_log.Information("combined_pickdecode_alt_head Sid={Sid} Node={Node} — combined decode pinned to alternate head",
							item.SessionId, dw.Name);
					else
						_log.Warning("combined_pickdecode_no_head Sid={Sid} — no CombinedCapable head free for combined decode (refusing to fall back to non-combined worker)",
							item.SessionId);
				}
			}
			else
			{
				dw = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health,
					item.PrefillWorker?.Name, allowedModels: _cfg.AllowedModels);
			}
			if (dw == null && item.PrefillWorker?.CanDecode == true
				&& (item.PrefillWorker.CombinedCapable || !combinedReq))
			{
				dw = item.PrefillWorker;
				CoordinatorMetrics.DecodeFallbackTotal.WithLabels("no_pd_worker_free").Inc();
				_log.Warning("decode_fallback_no_pd_worker_free Sid={Sid} PrefillNode={Pf} — no P/D-capable decode worker available, decoding on prefill node",
					item.SessionId, dw.Name);
			}
		}

		if (dw == null)
			return WorkItemState.None;

		// #470 Increment 2 (relay): the KV stream is consumed ONCE by the decode
		// RPC. A same-node pick would deadlock — the DECODE task queues behind
		// the still-streaming PREFILL task on the engine's single queue, and the
		// prefill stream can't finish until the decode consumes the channel.
		// Fail with a precise error instead of wedging the session.
		if (item.RelayChannel != null && item.PrefillWorker != null && dw.Name == item.PrefillWorker.Name)
		{
			_log.Error("relay_same_node_fallback Sid={Sid} Node={Node} — relay requires a decode node distinct from the prefill node, aborting",
				item.SessionId, dw.Name);
			item.Error = new InvalidOperationException(
				$"KV relay requires a decode node distinct from the prefill node (prefill={item.PrefillWorker.Name}, decode={dw.Name})");
			return WorkItemState.Failed;
		}

		// Atomic reuse path: the cold_atomic route already holds the slot via
		// item.DecodeLease (PrefillLease is null by design). Re-acquiring via
		// TryAcquireSlot would fail against our own lease; skip re-acquisition
		// and jump straight to the same-node skip decision.
		bool leaseReused = false;
		int slot = -1;
		if (item.RouteType == "cold_atomic"
			&& item.DecodeLease != null
			&& item.DecodeLease.WorkerName == dw.Name
			&& item.DecodeSlot == item.DecodeLease.SlotId)
		{
			leaseReused = true;
			slot = item.DecodeLease.SlotId;
		}

		if (!leaseReused && !_tracker.TryAcquireSlot(dw.Name, out slot, "decode"))
		{
			// No free decode slots — evict oldest warm lease and retry
			if (_warmLeases.Count > 0)
			{
				var oldest = _warmLeases.OrderBy(kv => kv.Value.CreatedAt).First();
				_log.Information("evicting_warm_decode Sid={Sid} Worker={W} Slot={Slot}",
					oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId);
				await SaveSlotStateBeforeEvictAsync(oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId, item.TraceId, default);
				await oldest.Value.DisposeAsync();
				_warmLeases.TryRemove(oldest.Key, out _);
				_ledger.MarkEvicted(oldest.Key);
				return WorkItemState.None; // retry via dispatch loop
			}

			return WorkItemState.None;
		}

		if (!leaseReused)
		{
			item.DecodeWorker = dw;
			item.DecodeSlot = slot;
			item.DecodeLease = new SlotLease(dw.Name, slot, item.SessionId,
				LeaseLifetime.Long, _tracker);
			LastDispatchedNode = dw.Name;
		}

		// #631: a MIGRATED session's PrefillWorker is derived from the LEDGER
		// (the stale source-node entry), NOT from a prefill that ran this turn.
		// Even when the decode worker lands back on the same node (primary pick
		// busy → PickBestDecodeWorker fallback to item.PrefillWorker), the slot
		// does NOT hold this session's KV — the migrate StatePut wrote it to the
		// target slot and MarkEvicted freed it; the continuation must re-restore
		// from Store. Skipping here would send the merged DECODE 0x43 with EMPTY
		// kv_metadata (fresh WorkItem, Kv* never populated) and no KV blob →
		// Gate A rejects (Tok=False Name=False) → 503 "KV not restored". Fall
		// through to ModelLoadDecode → RestoreKvAsync so the blob-manifest
		// repopulates the KV identity and the merged frame carries both.
		if (item.PrefillWorker?.Name == dw.Name && item.RouteType == "migration")
		{
			_log.Information("same_node_migrated_restore_required Sid={Sid} Node={Node} Slot={Slot} — migrated continuation, KV restore required",
				item.SessionId, dw.Name, slot);
		}
		// Same-node skip: when decode == prefill and no model switch,
		// the KV state is already on the node — no restore needed.
		//
		// M-Perf.9 #289: the alias-equality check is necessary but not
		// sufficient. The operator can swap the GGUF file behind a
		// stable alias (e.g. rebuild Balanced.gguf on disk) — the alias
		// stays "balanced" but the model identity changes. When the slot
		// carries a different identity from the KV the prefill built,
		// we must NOT skip — fall through to restore so the cross-model
		// guard in RestoreKvAsync can catch it.
		if (item.PrefillWorker?.Name == dw.Name
			&& item.RouteType != "migration"
			&& (!_cfg.MixPrecisionEnabled
				|| Router.DecodeModel(dw) == null
				|| Router.DecodeModel(dw) == Router.PrefillModel(item.PrefillWorker!)))
		{
			// Alias says same; verify the model identity actually matches.
			// Both-empty (pre-#470 or no metadata) skips the identity check
			// for back-compat — falls back to the old alias-only skip.
			bool aliasSaysSame = Router.DecodeModel(dw) == null
				|| Router.DecodeModel(dw) == Router.PrefillModel(item.PrefillWorker!);
			bool canCheckIdentity = !item.GetKvModelIdentity().IsEmpty;
			if (!aliasSaysSame || !canCheckIdentity)
			{
				_log.Information("same_node_skip Sid={Sid} Node={Node} — KV already resident (alias check)",
					item.SessionId, dw.Name);
				return WorkItemState.Decode;
			}
			try
			{
				// Item.PrefillSlot is the slot the prefill wrote to; same
				// worker, possibly same slot. Query its META to read the
				// current resident model identity.
				var prefillSlotId = item.PrefillSlot ?? slot;
				// CancellationToken.None: the META query is best-effort and
				// the try-catch below swallows failures. Plumbing ct
				// through PickDecodeAsync would cascade to 5+ call sites
				// and the next-step state machine for a non-critical read.
				var slotMeta = await GetLlamaClient(dw).GetStateMetaAsync(prefillSlotId, default);
				var slotIdentity = new ModelIdentity
				{
					Tokenizer = slotMeta.Tokenizer ?? "",
					ModelName = slotMeta.ModelName ?? "",
					ModelQuant = slotMeta.ModelQuant ?? "",
					ModelCapabilities = slotMeta.ModelCapabilities,
				};
				if (slotIdentity.IsEmpty
					|| item.GetKvModelIdentity() == slotIdentity)
				{
					_log.Information("same_node_skip Sid={Sid} Node={Node} Slot={Slot} — KV already resident (identity match)",
						item.SessionId, dw.Name, prefillSlotId);
					return WorkItemState.Decode;
				}
				_log.Information("same_node_skip_identity_mismatch Sid={Sid} Node={Node} Slot={Slot} stored={Stored} resident={Resident} — falling through to restore for cross-model guard",
					item.SessionId, dw.Name, prefillSlotId, item.KvModelName, slotMeta.ModelName);
			}
			catch (Exception ex)
			{
				// META query failed (older binary, transient error). Fall
				// through to the old behaviour — the cross-model guard
				// in RestoreKvAsync will catch mismatches if META is
				// reachable there.
				_log.Warning(ex, "same_node_skip_meta_failed Sid={Sid} Node={Node} — falling back to alias-only check",
					item.SessionId, dw.Name);
				return WorkItemState.Decode;
			}
		}
		return WorkItemState.ModelLoadDecode;
	}

	private async Task<WorkItemState> RestoreKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		var entry = _ledger.Lookup(item.SessionId);
		var slotId = Math.Min(
			item.DecodeSlot ?? item.PrefillSlot ?? entry?.SlotId ?? 0,
			w.Slots - 1);
		item.DecodeSlot = slotId; // Sync clamped slot so DecodeAsync pins the same one

		// #470 Increment 2 (relay): the KV is streaming live from the PREFILL
		// RPC (bounded channel) — no Store manifest / GET_CHUNKED on this path.
		// kv_len + whole-segment hash + model identity were captured by the
		// relay task's onMeta/onPayloadLen callbacks; the decode frame is fully
		// determined without touching the Store.
		if (item.RelayChannel != null)
		{
			item.KvTotalSize = item.RelayKvTotalSize;
			item.KvChunks = null;
			item.KvRestoredForDecode = true;
			_log.Information("restore_kv_relay Sid={Sid} Node={Node} Slot={Slot} Bytes={Bytes}",
				item.SessionId, w.Name, slotId, item.KvTotalSize);
			return WorkItemState.Decode;
		}

		// HYDRA_COORD_NO_STORE_KV_RESTORE=true: skip Store KV restore entirely.
		// The session slot already has KV from the prefill; we go straight to
		// decode. Combined with cache-prompt=true at the engine, this means
		// prompt caching is still available inside the slot but no Hydra-level
		// Store round-trip.
		//
		// Safety: when the decode worker differs from the session's affinity
		// node (cross-node fallback), the KV state cannot be transferred — the
		// slot has no KV from the prefill.  Returning Decode would leave the
		// engine trying to decode on a cold/no-KV slot, which hangs.  Release
		// the lease and re-route so the request retries on the correct node.
		if (_cfg.NoStoreKvRestore)
		{
			if (entry != null && !string.IsNullOrWhiteSpace(entry.NodeName)
				&& entry.NodeName != w.Name)
			{
				_log.Warning("restore_kv_abort_cross_node_nokv Sid={Sid} From={From} To={To}",
					item.SessionId, entry.NodeName, w.Name);
				if (item.DecodeLease != null)
					await item.DecodeLease.DisposeAsync();
				item.DecodeLease = null;
				item.DecodeWorker = null;
				item.DecodeSlot = null;
				item.State = WorkItemState.RouteDecision;
				return WorkItemState.None;
			}

			_log.Information("restore_kv_skipped Sid={Sid} Node={Node} Slot={Slot} (NoStoreKvRestore=true)",
				item.SessionId, w.Name, slotId);
			return WorkItemState.Decode;
		}

		var storeKey = $"{item.SessionId}.kv";
		_log.Information("restore_kv_start Sid={Sid} Key={Key} Node={Node} Slot={Slot}",
			item.SessionId, storeKey, w.Name, slotId);
		try
		{
			var restoreSw = System.Diagnostics.Stopwatch.StartNew();
			// #470 Phase 2: null when the KV is streamed from the Store instead
			// of assembled (merged-capable decode, chunks mode).
			byte[]? restoreBlob = null;
			if (_cfg.EnableChunks)
			{
				// ── Restore Phase 1: GetManifest ──────────────────────
				var manifestSw = System.Diagnostics.Stopwatch.StartNew();
				var manifestResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.GetManifest,
					storeKey, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
				if (manifestResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
					throw new InvalidOperationException($"GetManifest failed: status={manifestResp.Status} meta={manifestResp.Meta}");
				manifestSw.Stop();

				var manifestDoc = JsonDocument.Parse(manifestResp.Payload);
				var manifestRoot = manifestDoc.RootElement;
				var nPast = manifestRoot.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0;
				var totalSize = manifestRoot.TryGetProperty("total_size", out var ts) ? ts.GetInt64() : 0L;
				var manifestChunks = new List<ChunkRef>();
				if (manifestRoot.TryGetProperty("chunks", out var chunksEl) && chunksEl.ValueKind == JsonValueKind.Array)
				{
					foreach (var c in chunksEl.EnumerateArray())
					{
						var idx = c.GetProperty("index").GetInt32();
						var hash = c.GetProperty("hash").GetString() ?? "";
						var size = c.GetProperty("size").GetInt32();
						manifestChunks.Add(new ChunkRef(idx, hash, size));
					}
				}
				if (manifestRoot.TryGetProperty("model_alias", out var ma))
					item.KvModelAlias = ma.GetString();
				if (manifestRoot.TryGetProperty("tokenizer", out var tk))
					item.KvTokenizer = tk.GetString();
				if (manifestRoot.TryGetProperty("model_name", out var mn))
					item.KvModelName = mn.GetString();
				if (manifestRoot.TryGetProperty("model_quant", out var mq))
					item.KvModelQuant = mq.GetString();
				if (manifestRoot.TryGetProperty("model_capabilities", out var mc) && mc.ValueKind == JsonValueKind.Number)
					item.KvModelCapabilities = mc.GetUInt32();
				if (manifestRoot.TryGetProperty("model_path", out var mp))
					item.KvModelPath = mp.GetString();
				if (item.NPastAfter > 0) nPast = item.NPastAfter;
				else item.NPastAfter = nPast;

				// ── Restore Phase 2: chunks (streamed) or assembled blob ──
				// #470 Phase 2: when the decode engine is merged-capable, the KV
				// is streamed from the Store into the framed DECODE — the full
				// blob is never assembled in RAM. Only the non-merged StatePut
				// path needs the assembled byte[].
				var mergedCapableForRestore = _health.GetNodeInfo(w.Name)?.EngineCapabilities?.Contains(Protocol.CapMergedDecode) == true;
				if (mergedCapableForRestore)
				{
					item.KvChunks = manifestChunks;
					item.KvTotalSize = totalSize;
					item.KvBytes = totalSize;
					_log.Information("restore_kv_stream_planned Sid={Sid} SizeMB={Size} Chunks={Count} manifest_ms={ManifestMs}",
						item.SessionId, totalSize / 1024 / 1024, manifestChunks.Count,
						manifestSw.ElapsedMilliseconds);
				}
				else
				{
					var assembleSw = System.Diagnostics.Stopwatch.StartNew();
					restoreBlob = await AssembleFromChunksAsync(null, storeKey, manifestChunks, item.TraceId, ct);
					assembleSw.Stop();
					item.KvBytes = restoreBlob.Length;
					_log.Information("state_assembled Sid={Sid} SizeMB={Size} Chunks={Count} manifest_ms={ManifestMs} assemble_ms={AssembleMs}",
						item.SessionId, restoreBlob.Length / 1024 / 1024, manifestChunks.Count,
						manifestSw.ElapsedMilliseconds, assembleSw.ElapsedMilliseconds);
				}
			}
			else
			{
				var storeResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Get,
					storeKey, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);

				if (storeResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
					throw new InvalidOperationException($"Store Get RPC failed: status={storeResp.Status} meta={storeResp.Meta}");

				if (item.KvBytes == 0)
					item.KvBytes = storeResp.Payload.Length;
				restoreBlob = storeResp.Payload;
			}

			// ── Restore Phase 3: StatePut RPC (push KV to engine) ─────
			// #470: When the engine advertises merged_decode, skip the blind
			// STATE_PUT and instead carry the KV blob in the framed DECODE 0x43
			// RPC. This merges the unvalidated STATE_PUT + HTTP decode into a
			// single validated call — the engine checks model identity BEFORE
			// restoring KV. Without this guard, item.KvBlob would be null by
			// DecodeAsync (consumed + nulled in SaveKvAsync), so the framed
			// DECODE would send empty bytes and the engine would skip restore.
			var mergedCapable = _health.GetNodeInfo(w.Name)?.EngineCapabilities?.Contains(Protocol.CapMergedDecode) == true;
			if (mergedCapable)
			{
				if (restoreBlob != null)
				{
					// Carry the assembled KV blob forward to DecodeAsync so the
					// framed DECODE 0x43 can send it as part of the merged RPC.
					item.KvBlob = restoreBlob;
					_log.Information("restore_kv_merged_skip_state_put Sid={Sid} Node={Node} Slot={Slot} BlobMB={MB}",
						item.SessionId, w.Name, slotId, restoreBlob.Length / 1024 / 1024);
				}
				else
				{
					// #470 Phase 2: no blob assembled — DecodeAsync streams the
					// KV from the Store (item.KvChunks) inside the framed DECODE.
					_log.Information("restore_kv_merged_stream_from_store Sid={Sid} Node={Node} Slot={Slot} Bytes={Bytes}",
						item.SessionId, w.Name, slotId, item.KvTotalSize);
				}
			}
			else
			{
			var putSw = System.Diagnostics.Stopwatch.StartNew();
			var llamaRpc = GetStateRpcClient(w);
			var putResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StatePut,
				slotId.ToString(), restoreBlob, item.TraceId, ct);
			putSw.Stop();

			if (putResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
				throw new InvalidOperationException($"StatePut RPC failed: status={putResp.Status} meta={putResp.Meta}");

			// #469 trace: log STATE_PUT response for cross-flow comparison
				_log.Debug("#PD-TRACE STATE_PUT_RESPONSE Sid={Sid} Node={Node} Slot={Slot} Status={Status} Meta={Meta} BlobSize={BlobSize}",
				item.SessionId, w.Name, slotId, putResp.Status, putResp.Meta ?? "(null)", restoreBlob.Length);

			_log.Information("restore_kv_timing Sid={Sid} total_ms={TotalMs} put_ms={PutMs}",
				item.SessionId, restoreSw.ElapsedMilliseconds, putSw.ElapsedMilliseconds);

			if (putResp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(putResp.Meta);
				item.NPastAfter = meta?.TryGetValue("n_past", out var n) == true ? n.GetInt32() : item.NPastAfter;

				// M-Perf.9 #289 + #470: parse model identity from STATE_PUT
				// response. The engine returns model_match, tokenizer, model_name,
				// model_quant, model_capabilities, model_alias, and model_path so
				// we can check cross-model safety *before* proceeding to HTTP
				// decode — eliminating the race where the KV was already written
				// to the slot before the STATE_META query confirmed it.
				var modelMatch = meta?.TryGetValue("model_match", out var mm) == true
					? mm.GetBoolean()
					: true; // back-compat: old servers omit field → assume match
				var slotTokenizer = meta?.TryGetValue("tokenizer", out var tk) == true
					? tk.GetString()
					: null;
				var slotModelName = meta?.TryGetValue("model_name", out var smn) == true
					? smn.GetString()
					: null;
				var slotModelQuant = meta?.TryGetValue("model_quant", out var smq) == true
					? smq.GetString()
					: null;
				uint slotCapabilities = meta?.TryGetValue("model_capabilities", out var sc) == true
					&& sc.ValueKind == JsonValueKind.Number ? sc.GetUInt32() : 0;
				var slotAlias = meta?.TryGetValue("model_alias", out var ma) == true
					? ma.GetString()
					: null;
				var slotPath = meta?.TryGetValue("model_path", out var mp) == true
					? mp.GetString()
					: null;

				// #479/S3: the decode worker's resident model is only learned
				// here (STATE_PUT meta) — stamp it so request_timeline's
				// decode_model field and AutoRouter residency see it. Empty
				// fields are ignored by UpdateNodeModelIdentity.
				if (!string.IsNullOrEmpty(slotAlias))
				{
					_health.UpdateNodeModelIdentity(w.Name, slotAlias,
						slotTokenizer ?? "", slotModelName ?? "", slotModelQuant ?? "", slotCapabilities);
				}

				// #470: model_match is the engine-side stub (always true for now).
				// CrossModelGuard.Decide is the authoritative identity comparison —
				// it covers the case where model_match=true but the stored KV's
				// identity differs from the slot's resident identity (the
				// exact corruption class #469 investigated). This replaces the
				// old post-hoc STATE_META query with a single atomic check.
				if (modelMatch)
				{
					var storedIdentity = item.GetKvModelIdentity();
					var slotIdentity = new ModelIdentity
					{
						Tokenizer = slotTokenizer ?? "",
						ModelName = slotModelName ?? "",
						ModelQuant = slotModelQuant ?? "",
						ModelCapabilities = slotCapabilities,
					};
					var guard = CrossModelGuard.Decide(
						stored: storedIdentity,
						slot: slotIdentity,
						allowCrossModelKvReuse: _cfg.AllowCrossModelKvReuse);

					switch (guard)
					{
						case CrossModelGuard.Outcome.Proceed:
							_log.Debug("cross_model_kv_proceeded slot={Slot} stored_name={StoredName} slot_name={SlotName}",
								slotId, item.KvModelName, slotModelName);
							CoordinatorMetrics.CrossModelKvProceeded.WithLabels(w.Name).Inc();
							break;
						case CrossModelGuard.Outcome.Skip:
							_log.Debug("cross_model_kv_skipped slot={Slot} stored_name={StoredName} slot_name={SlotName}",
								slotId, item.KvModelName, slotModelName);
							CoordinatorMetrics.CrossModelKvSkipped.WithLabels(w.Name).Inc();
							break;
						case CrossModelGuard.Outcome.WarnAndProceed:
							_log.Warning("cross_model_kv_warned slot={Slot} stored_name={StoredName} slot_name={SlotName} item={Item}",
								slotId, item.KvModelName, slotModelName, item.SessionId);
							CoordinatorMetrics.CrossModelKvWarned.WithLabels(w.Name).Inc();
							break;
						case CrossModelGuard.Outcome.Abort:
							_log.Warning("cross_model_kv_aborted slot={Slot} stored_name={StoredName} slot_name={SlotName} item={Item} — re-prefilling",
								slotId, item.KvModelName, slotModelName, item.SessionId);
							CoordinatorMetrics.CrossModelKvAborted.WithLabels(w.Name).Inc();
							// Fall through to the shared erase+re-prefill path below
							modelMatch = false;
							break;
					}
				}
				else
				{
					// Engine-side model_match=false: the engine already rejected
					// the KV (future path when KV header carries model identity).
					_log.Warning("state_put_model_mismatch slot={Slot} stored_name={StoredName} slot_name={SlotName} item={Item}",
						slotId, item.KvModelName, slotModelName, item.SessionId);
					CoordinatorMetrics.CrossModelKvAborted.WithLabels(w.Name).Inc();
				}

				if (!modelMatch)
				{
					// Shared abort path: erase the slot we just wrote into and
					// re-prefill on the correct model.
					// Erase the slot we just wrote into (same as the
					// existing Abort path — otherwise the same-node skip
					// would trust the now-incorrect slot).
					try
					{
						await GetLlamaClient(w).EraseSlotAsync(slotId, ct);
					}
					catch (Exception eraseEx)
					{
						_log.Warning(eraseEx, "state_put_erase_failed Slot={Slot}", slotId);
					}
					if (item.DecodeLease != null)
					{
						await item.DecodeLease.DisposeAsync();
						item.DecodeLease = null;
						SignalEvaluator();
					}
					if (item.PrefillWorker?.CanDecode == true
						&& _tracker.TryAcquireSlot(item.PrefillWorker.Name, out var fbSlot2, "decode-fallback"))
					{
						item.DecodeWorker = item.PrefillWorker;
						item.DecodeSlot = fbSlot2;
						item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot2, item.SessionId,
							LeaseLifetime.Long, _tracker);
						CoordinatorMetrics.DecodeFallbackTotal.WithLabels("cross_model_abort").Inc();
					}
					item.NPastAfter = 0;
					item.KvRestoredForDecode = false;
					CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
						.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
					return WorkItemState.Prefill;
				}

				_log.Debug("state_put_validation_ok slot={Slot} model={Alias} path={Path} match={Match}",
					slotId, slotAlias, slotPath, modelMatch);
			}
			} // end else (!mergedCapable)

		_log.Information("state_restored Sid={Sid} NPast={N} Node={Node}",
			item.SessionId, item.NPastAfter, w.Name);
		item.KvRestoredForDecode = true;
		}
		catch (Exception ex)
		{
			if (item.PrefillWorker?.CanDecode == true
				&& item.DecodeWorker?.Name != item.PrefillWorker?.Name
				&& _tracker.TryAcquireSlot(item.PrefillWorker.Name, out var fbSlot, "decode-fallback"))
			{
				_log.Warning(ex, "restore_failed_fallback Sid={Sid} Node={Failed} → {Fallback}",
					item.SessionId, item.DecodeWorker?.Name, item.PrefillWorker.Name);
				if (item.DecodeLease != null)
				{
					await item.DecodeLease.DisposeAsync();
					item.DecodeLease = null;
					SignalEvaluator();
				}
				item.DecodeWorker = item.PrefillWorker;
				item.DecodeSlot = fbSlot;
				item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				CoordinatorMetrics.DecodeFallbackTotal.WithLabels("restore_fail").Inc();
				CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
				return WorkItemState.Decode;
			}
			// No fallback available: decoding with NPast=0 on a potentially
			// cross-model node would either hang or produce garbage. Abort
			// and re-route so the request retries on a compatible node.
			_log.Warning(ex, "restore_skipped_abort Sid={Sid} Node={Node} — no fallback, re-routing",
				item.SessionId, w.Name);
			if (item.DecodeLease != null)
			{
				await item.DecodeLease.DisposeAsync();
				item.DecodeLease = null;
				SignalEvaluator();
			}
			item.DecodeWorker = null;
			item.DecodeSlot = null;
			item.NPastAfter = 0;
			item.KvRestoredForDecode = false;
			item.State = WorkItemState.RouteDecision;
			CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
				.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
			return WorkItemState.None;
		}
		if (item.NPastAfter > 0)
			_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
		_ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter > 0 ? item.NPastAfter : entry?.NPast ?? 0, item.PrefixHash);
		CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
		return WorkItemState.Decode;
	}

	/// <summary>
	/// Resolve the token budget for the merged-decode (0x43) request.
	/// Precedence mirrors llama-server's own parsing in
	/// params_from_json_cmpl (n_predict → max_completion_tokens → max_tokens)
	/// so the merged path and the HTTP fallback agree; the merged path must
	/// honor the client's limit or a failure degrades to a very long decode
	/// instead of a bounded reply (#581).
	/// </summary>
	internal static int GetMergedDecodeNPredict(WorkItem item)
	{
		if (item.Request.TryGetValue("n_predict", out var npEl) && TryAsInt(npEl, out var n))
			return n;
		if (item.Request.TryGetValue("max_completion_tokens", out var mctEl) && TryAsInt(mctEl, out var mct))
			return mct;
		if (item.Request.TryGetValue("max_tokens", out var mtEl) && TryAsInt(mtEl, out var mt))
			return mt;
		return 2048;

		static bool TryAsInt(object? value, out int result)
		{
			switch (value)
			{
				case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var i):
					result = i;
					return true;
				case int i:
					result = i;
					return true;
				case long l when l is >= int.MinValue and <= int.MaxValue:
					result = (int)l;
					return true;
				default:
					result = 0;
					return false;
			}
		}
	}

	/// <summary>
	/// Build the merged-decode (0x43) "prompt segment" as a JSON object
	/// containing messages + optional tools/tool_choice/response_format +
	/// optional sampling/stop. The engine's DECODE_APPLY handler
	/// (server-context.cpp) reads tools/tool_choice/response_format off the
	/// prompt object directly, and the nested sampling object
	/// (temperature/top_p/top_k/seed) plus sibling "stop" array. When none of
	/// those are present the output is {"messages": [...]}, semantically
	/// identical to what the engine's bare-array-wrap produces for the legacy
	/// messages-only case — the common path is unchanged.
	///
	/// Tool/response-format schemas can be large, so they belong here in the
	/// prompt segment (arbitrary length, its own segment framing) rather than
	/// the RPC control header (hard-capped at 32 KiB in RpcClient.cs).
	/// </summary>
	internal static string? BuildMergedDecodePromptSegment(WorkItem item)
	{
		if (!item.Request.TryGetValue("messages", out var messagesEl))
			return null;

		var segment = new Dictionary<string, object?> { ["messages"] = messagesEl };

		// #581: n_predict must ride in the prompt segment itself — DECODE_APPLY
		// reads prompt.value("n_predict", 256) directly, and some engine
		// revisions do not merge the control header's "generation" object into
		// the prompt. Always emit it so the segment and the header generation
		// object agree (both carry the same resolved budget); without it the
		// engine decodes until EOS instead of honoring the client's limit.
		segment["n_predict"] = GetMergedDecodeNPredict(item);

		if (item.Request.TryGetValue("tools", out var toolsEl))
			segment["tools"] = toolsEl;
		if (item.Request.TryGetValue("tool_choice", out var toolChoiceEl))
			segment["tool_choice"] = toolChoiceEl;
		if (item.Request.TryGetValue("response_format", out var responseFormatEl))
			segment["response_format"] = responseFormatEl;

		var ov = item.RequestOverrides;
		if (ov is not null)
		{
			// NOTE: keys here are DECODE_APPLY's wire shape (temperature/top_p/
			// top_k/seed, sibling "stop") — NOT the same as
			// EngineRequestOverrides.ToWireJson()'s 0x40-CONFIGURE shape
			// (temp/antiprompt). Do not reuse ToWireJson() here.
			var sampling = new Dictionary<string, object>();
			if (ov.Temperature is { } temperature) sampling["temperature"] = temperature;
			if (ov.TopP is { } topP) sampling["top_p"] = topP;
			if (ov.TopK is { } topK) sampling["top_k"] = topK;
			if (ov.Seed is { } seed) sampling["seed"] = seed;
			if (sampling.Count > 0) segment["sampling"] = sampling;

			if (ov.Stop is { Count: > 0 } stop) segment["stop"] = stop;
		}

		return JsonSerializer.Serialize(segment);
	}

	/// <summary>
	/// #470/#598: resolve everything the merged-decode (0x43) request needs
	/// that is shared between the streaming and non-streaming decode paths:
	/// the prompt segment (messages + tools/sampling/stop, #576), the
	/// n_predict budget (#581), the decode node's own model identity from
	/// STATE_META (the only independent source for Gate A, #470/A7), and the
	/// GGUF-file alias for the engine's model-swap lookup (#589). Callers
	/// still own the EngineMergedDecodeAsync RPC itself — it differs only in
	/// the stream flag and cancellation token.
	/// </summary>
	private async Task<(string? MessagesJson, int NPredict, ModelIdentity Identity, string? ModelAlias)>
		ResolveMergedDecodeRequestAsync(WorkItem item, WorkerConfig w, CancellationToken ct)
	{
		// #576: tools/tool_choice/response_format/sampling/stop travel
		// inside the prompt-segment object (not the samplingJson
		// parameter / 32 KiB control header).
		var messagesJson = BuildMergedDecodePromptSegment(item);
		var nPredict = GetMergedDecodeNPredict(item);

		// #470/A7: model_metadata (what the decode node is actually running)
		// comes from querying the decode node's own STATE_META — the only
		// truly independent source. This avoids the tautology where both
		// sides trace back to item.Kv* or HealthMonitor (which was stamped
		// from item.Kv* during PrefillAsync).
		var modelIdentity = ModelIdentity.Empty;
		SlotMeta? decodeSlotMeta = null;
		try
		{
			using var metaCts = new CancellationTokenSource(DecodeMetaQueryTimeout);
			decodeSlotMeta = await GetLlamaClient(w).GetStateMetaAsync(
				item.DecodeSlot ?? item.LastIdSlot ?? 0, metaCts.Token);
			if (!string.IsNullOrEmpty(decodeSlotMeta.ModelName))
			{
				modelIdentity = new ModelIdentity
				{
					Tokenizer = decodeSlotMeta.Tokenizer,
					ModelName = decodeSlotMeta.ModelName,
					ModelQuant = decodeSlotMeta.ModelQuant,
					ModelCapabilities = decodeSlotMeta.ModelCapabilities,
				};
				// #479/S3: stamp the decode node's resident identity so
				// request_timeline decode_model reflects what the engine is
				// actually running (META is the authoritative source).
				if (!string.IsNullOrEmpty(decodeSlotMeta.ModelAlias))
				{
					_health.UpdateNodeModelIdentity(w.Name, decodeSlotMeta.ModelAlias,
						decodeSlotMeta.Tokenizer, decodeSlotMeta.ModelName,
						decodeSlotMeta.ModelQuant, decodeSlotMeta.ModelCapabilities);
				}
			}
		}
		catch (Exception ex)
		{
			// #470/A7: META query failed — send empty model_metadata so
			// Gate A rejects on its own terms. Falling back to kvIdentity
			// would recreate the tautology (comparing the same identity
			// against itself), defeating the entire guard.
			_log.Warning(ex, "gate_a_decode_meta_query_failed Sid={Sid} Worker={W} Slot={Slot}",
				item.SessionId, w.Name, item.DecodeSlot ?? item.LastIdSlot ?? 0);
		}

		// #470: preset_alias_to_path is keyed by GGUF-file aliases
		// (e.g. "balanced"), not routing aliases (e.g. "moe-35b-pd").
		// ResolveMergedDecodeModelAlias translates the routing identity to
		// the correct GGUF-file alias for the engine's model-swap lookup.
		// Fix 2 (#470): the META-returned resident alias (the model the
		// decode node is ACTUALLY running — the same source that built
		// model_metadata above) takes precedence so the frame's `model`
		// and `model_metadata` always describe the same model — the engine
		// never receives a self-contradictory alias-vs-identity frame.
		// #631: for MIGRATED continuations the health-stamped resident alias
		// (CurrentModel — the worker's engine-reported resident, same source
		// family as STATE_META) backs up the META alias, so a cross-quant
		// migration still sends an alias that maps to the target's resident
		// path even when the STATE_META query fails.
		var modelAlias = ResolveMergedDecodeModelAlias(item, w, decodeSlotMeta?.ModelAlias,
			healthResidentAlias: _health.GetNodeInfo(w.Name)?.CurrentModel);

		return (messagesJson, nPredict, modelIdentity, modelAlias);
	}

	/// <summary>
	/// #470 Fix 3: re-assert the session's decode lease after a
	/// <c>merged_decode_transport_fault</c>. The HTTP-proxy fallback streams
	/// (or buffers) the reply, but the session's slot binding must survive so
	/// the slot is returned to the pool when the request completes — a lost
	/// lease shows up as <c>stream_done_no_lease</c> in NotifyStreamComplete
	/// and strands the slot, starving the next turn of this session (empty /
	/// 503 replies). No-op when the lease is still held; otherwise acquires a
	/// fresh Long lease on the decode worker and re-pins <c>id_slot</c> so the
	/// fallback request and the eventual release use the same slot.
	/// </summary>
	private async Task ReassertDecodeLeaseAsync(WorkItem item, WorkerConfig w)
	{
		if (item.DecodeLease != null)
			return;

		var priorSlot = item.DecodeSlot ?? item.LastIdSlot ?? 0;
		if (_tracker.TryAcquireSlot(w.Name, out var reSlot, "decode-fault"))
		{
			item.DecodeSlot = reSlot;
			item.DecodeLease = new SlotLease(w.Name, reSlot, item.SessionId,
				LeaseLifetime.Long, _tracker);
			item.Request["id_slot"] = reSlot;
			_log.Warning("merged_decode_fault_lease_reasserted Sid={Sid} Worker={W} PriorSlot={P} Slot={Slot}",
				item.SessionId, w.Name, priorSlot, reSlot);
			SignalEvaluator();
		}
		else
		{
			_log.Warning("merged_decode_fault_lease_reassert_failed Sid={Sid} Worker={W} — no free slot, HTTP fallback proceeds without a lease",
				item.SessionId, w.Name);
		}
	}

	// ── Gap 4: n_past tracking from decode ──
	private async Task<WorkItemState> DecodeAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		var msgCount = item.Messages?.Count ?? 0;
		var lastMsg = msgCount > 0 && item.Messages[^1].TryGetValue("content", out var c) && c != null
			? (c.ToString() ?? "")[..Math.Min(80, (c.ToString() ?? "").Length)]
			: "?";
		var mt = item.Request.TryGetValue("max_tokens", out var mtv) ? mtv?.ToString() : "?";

		// #616: snapshot the CLEAN client request body BEFORE id_slot /
		// hydra_config / stream_options are injected, so the empty-content
		// merged-decode fallback re-issues the ORIGINAL request (same
		// messages/model/max_tokens). Only needed when the decode node may
		// take the merged path; a JSON round-trip makes the clone immune to
		// any later in-place mutation.
		var decodeNodeMergedCapable = _cfg.UseLlamaEngine
			&& _health.GetNodeInfo(w.Name)?.EngineCapabilities?.Contains(Protocol.CapMergedDecode) == true;
		var cleanRequestBody = decodeNodeMergedCapable
			? DeepCloneRequestBody(item.Request)
			: null;

		// Pin decode to the leased slot so llama-server doesn't pick a different one via LRU
		if (item.DecodeSlot.HasValue)
			item.Request["id_slot"] = item.DecodeSlot.Value;

		// #481 Phase 2c: hydra_config injection is driven by the request's
		// resolved model alias, NOT by the worker's static ModelAlias (which
		// is null for model-agnostic workers). Read the routing identity
		// directly from the `model` field.
		//
		// COMBINED mode: hydra_config was already applied during PrefillAsync
		// (same engine, same slot). Skip re-injection to avoid GGUF-alias
		// vs routing-identity mismatch in ResolveEngineConfig.
		var resolvedAlias = item.RouteType == "combined"
			? null
			: (item.Request.TryGetValue("model", out var m) ? m?.ToString() : null);
		if (!string.IsNullOrEmpty(resolvedAlias))
		{
			try
			{
				// Prefer ModelConfigLoader when available (data-driven config);
				// fall back to ModelRegistry (hardcoded entries) otherwise.
				EngineConfig? engineConfig = null;
				try
				{
					// FIX #443 P1: decode role uses DecodeAlias for mix-quant
					// P/D split (P100 loads Q5_K-balanced, not Q3_K-mini).
					var isDecodeRole = item.RouteType == "cold_pd";
					engineConfig = ModelConfigLoader.Instance.ResolveEngineConfig(resolvedAlias, isDecodeRole);
				}
				catch (InvalidOperationException)
				{
					// ModelConfigLoader not initialized or alias not found —
					// fall back to the static ModelRegistry. For the legacy
					// case the routing identity IS the ModelRegistry key.
					engineConfig = ModelRegistry.Resolve(resolvedAlias);
				}
				if (engineConfig is not null)
				{
					var hydraConfig = engineConfig.ToHydraConfigDict();
					if (hydraConfig.Count > 0)
						item.Request["hydra_config"] = hydraConfig;
				}
			}
			catch (Exception ex)
			{
				_log.Error("hydra_config injection failed Alias={Alias}: {Error}", resolvedAlias, ex.Message);
			}
		}
		else
		{
			_log.Warning("hydra_config skipped: no resolved model alias in request for Node={Node}", w.Name);
		}

		Console.Error.WriteLine($"event=decode_body Sid={item.SessionId} " +
			System.Text.Json.JsonSerializer.Serialize(item.Request));
		_log.Information("decode_start Sid={Sid} Node={Node} Msgs={Msgs} LastMsg={Last} Streaming={Stream} NPast={N} MaxTokens={Mt} Slot={Slot}",
			item.SessionId, w.Name, msgCount, lastMsg, item.IsStreaming, item.NPastAfter, mt, item.DecodeSlot);
		EmitPartialTimeline(item, "decoding");
		if (item.IsStreaming)
		{
			// decode_ms is finalized in NotifyStreamComplete — the stream is still
			// running when this state returns Done.
			item.DecodeStartMs = item.ElapsedMs;
			var cts = CancellationTokenSource.CreateLinkedTokenSource(item.HttpCancellationToken, ct);
			_pipelineCts[item.SessionId] = cts;

			// Phase 2b: emit 0x40 EngineConfigure with per-request T1 overrides
			// (sampling, n_predict, seed) before activating the peer. T1 keys
			// apply immediately; the call is best-effort (a failure here is
			// logged + countered, the request continues with the engine's
			// current config — same fall-back pattern as SET_EXPERT_MODE).
			if (_cfg.UseLlamaEngine && item.RequestOverrides is { IsEmpty: false })
				await ApplyRequestOverridesAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, cts.Token);

			// Two-engine "work together": activate the peer before decode (no-op when solo).
			if (_cfg.UseLlamaEngine)
				await ApplyMultiEngineAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, cts.Token);

			// #470: merged decode path — when the engine advertises merged_decode,
			// send the framed DECODE 0x43 with kv_metadata + model_metadata + prompt
			// to get the decode_request_id and model identity match. On success,
			// poll GET /v1/decode/{id} for the streaming result (skips HTTP proxy).
			bool mergedDecodeOk = false;
			if (decodeNodeMergedCapable)
			{
				bool gateRejected = false;
				try
				{
					// #598: resolution shared with the non-streaming path —
					// prompt segment, n_predict budget, STATE_META model
					// identity (Gate A) and GGUF-file alias.
					var (messagesJson, nPredict, modelIdentity, modelAlias) =
						await ResolveMergedDecodeRequestAsync(item, w, cts.Token);

					// #470/A7: kv_metadata (what built the KV) comes from the
					// PREFILL response — not queryable on the decode node.
					var kvIdentity = item.GetKvModelIdentity();

					var llamaRpc = GetLlamaRpcClient(w);
					// #470 BUSY-retry (2026-08-13): an engine slot busy with a
					// concurrent decode returns HYDRA_STATUS_BUSY (0x04) and the
					// merged decode frame carries Status=Busy with Valid=false.
					// Previously ANY non-Ok status hit the gate → 503 "KV not
					// restored" — even though the slot merely needs a moment.
					// Retry bounded (3 attempts, 500ms/1s/2s backoff) BEFORE the
					// gate; only persistent Busy falls through to the abort.
					// Non-Busy rejects (identity mismatch etc.) stay terminal.
					MergedDecodeResponse? mergedResp = null;
					var busyAttempt = 0;
					const int busyMaxAttempts = 3;
					while (busyAttempt < busyMaxAttempts)
					{
						if (busyAttempt > 0)
						{
							var backoffMs = busyAttempt switch { 1 => 500, 2 => 1000, _ => 2000 };
							_log.Warning("merged_decode_busy_retry Sid={Sid} Node={Node} Attempt={A}/{Max} — slot busy, retrying in {Ms}ms",
								item.SessionId, w.Name, busyAttempt + 1, busyMaxAttempts, backoffMs);
							await Task.Delay(backoffMs, cts.Token);
						}
						if (item.RelayChannel != null)
						{
							// #470 Increment 2 (relay): KV streams live from the
							// PREFILL RPC — no Store read. The stream is consumed
							// ONCE: a BUSY reject cannot be retried (re-enumeration
							// would yield zero bytes and a kv_len mismatch), so
							// break out of the retry loop immediately.
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only
							// (excludes queue wait and KV restore, both separately observable).
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeStreamKvAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								samplingJson: null,
								stream: true,
								kvChunks: RelayKvChunksAsync(item.RelayChannel, cts.Token),
								kvTotalSize: item.KvTotalSize,
								kvHash: item.KvHash,
								traceId: item.TraceId,
								ct: cts.Token);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
							break;
						}
						if (item.KvChunks is { Count: > 0 })
						{
							// #470 Phase 2: KV streamed from the Store (no full
							// blob in RAM). The enumerable re-fetches the chunks
							// on every BUSY retry attempt.
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only.
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeStreamKvAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								// #576: sampling/stop now travel inside messagesJson
								// (the prompt segment), so the separate samplingJson
								// channel stays empty — the engine's generation-header
								// merge only fills keys the prompt object lacks.
								samplingJson: null,
								stream: true,
								kvChunks: StreamKvChunksFromStoreAsync(
									$"{item.SessionId}.kv", item.KvChunks, item.TraceId, cts.Token),
								kvTotalSize: item.KvTotalSize,
								kvHash: item.KvHash,
								traceId: item.TraceId,
								ct: cts.Token);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
						}
						else
						{
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only.
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								// #576: sampling/stop now travel inside messagesJson
								// (the prompt segment), so the separate samplingJson
								// channel stays empty — the engine's generation-header
								// merge only fills keys the prompt object lacks.
								samplingJson: null,
								stream: true,
								kvBlob: item.KvBlob ?? ReadOnlyMemory<byte>.Empty,
								traceId: item.TraceId,
								ct: cts.Token);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
						}
						if (mergedResp.Status != (byte)StatusCode.Busy)
							break; // Ok or terminal reject — no more retries
						busyAttempt++;
					}

					// #470 Increment 2 (relay): the PREFILL stream drained into
					// the decode leg — await the background prefill RPC so a
					// stream failure surfaces with the prefill RPC's error
					// (the decode write would have failed first in that case).
					if (item.RelayTask != null)
						await item.RelayTask;

					item.DecodeRequestId = mergedResp.DecodeRequestId;
					CoordinatorMetrics.DecodeRequestIdsIssued.Inc();
					item.Match = new DecodeMatch(
						mergedResp.TokenizerMatch,
						mergedResp.ModelNameMatch,
						mergedResp.ModelCapabilitiesMatch,
						mergedResp.CapabilitiesXor,
						mergedResp.ModelQuantMatch,
						mergedResp.ModelAliasMatch);

					_log.Information("merged_decode_initiated Sid={Sid} DecodeId={Did} Valid={V} NPast={N} RestoreMs={R} InitMs={I} Match=Tok={TM}Name={NM}Cap={CM}Quant={QM}Alias={AM}",
						item.SessionId, mergedResp.DecodeRequestId, mergedResp.Valid,
						mergedResp.NPastAfterRestore, mergedResp.RestoreSlotMs, mergedResp.DecodeInitMs,
						mergedResp.TokenizerMatch, mergedResp.ModelNameMatch,
						mergedResp.ModelCapabilitiesMatch, mergedResp.ModelQuantMatch,
						mergedResp.ModelAliasMatch);
					CoordinatorMetrics.RestoreSlotMs.WithLabels(w.Name).Observe(mergedResp.RestoreSlotMs);
					CoordinatorMetrics.DecodeInitMs.WithLabels(w.Name).Observe(mergedResp.DecodeInitMs);

					if (!mergedResp.Valid || mergedResp.DecodeRequestId <= 0)
					{
						// #470: Enforcing gate — the engine rejected the KV (identity
						// mismatch, slot busy, etc.). With merged_decode, RestoreKvAsync
						// skipped the blind STATE_PUT, so the slot is empty. Decoding
						// via HTTP proxy here would hit an empty/corrupt slot (the #469
						// hallucination scenario). Abort the entire request instead.
						gateRejected = true;
						throw new InvalidOperationException(
							$"DECODE 0x43 rejected Sid={item.SessionId} Valid={mergedResp.Valid} DecodeId={mergedResp.DecodeRequestId} — KV not restored, aborting");
					}
					else
					{
						// #470: Poll GET /v1/decode/{id} for the streaming result.
						// The engine generates asynchronously; the GET endpoint returns
						// 404 until the result is ready.
						item.Request["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
						IAsyncEnumerable<byte[]> mergedStream = _proxy.PollDecodeStreamAsync(
							w.LlamaUrl, mergedResp.DecodeRequestId!.Value, item.TraceId, cts.Token, item);

						// #616/#642/#588: the merged stream arms the empty-content probe —
						// if the engine generated but NEITHER content NOR
						// reasoning_content NOR tool_calls was seen (all three are
						// delivered in the merged DONE delta, engine 097d13e/b95c228b;
						// a reasoning- or tool-call-only reply must NOT trigger the
						// fallback), the stream is re-issued ONCE via the
						// HTTP proxy with the CLEAN client body.
						item.DecodeChunks = TrackStreamNPast(mergedStream, item,
							mergedPath: true, fallbackRequestBody: cleanRequestBody!,
							fallbackNodeUrl: w.LlamaUrl, fallbackCt: cts.Token);
						_pendingBgSaves[item.SessionId] = (w.Name, item.DecodeSlot ?? 0, item.TraceId);
						item.StreamCompletion.TrySetResult(item.DecodeChunks);
						item.Response = new { streamed = true };
						if (_ledger.Lookup(item.SessionId) == null)
							_ledger.Register(item.SessionId, w.Name, item.DecodeSlot, item.NPastAfter, item.PrefixHash);
						mergedDecodeOk = true;
					}
				}
				catch (Exception ex) when (!gateRejected && item.RelayChannel == null)
				{
					// #470 Increment 2 (relay): no HTTP-proxy fallback — the KV
					// stream was consumed once and cannot be replayed; a fault
					// fails the turn with the root error (no-fallback rule).
					_log.Warning(ex, "merged_decode_transport_fault Sid={Sid} — falling back to HTTP proxy",
						item.SessionId);
					// #470 Fix 3: a transport fault must never orphan the
					// session's decode slot. If the lease was lost before the
					// fallback streams, re-assert a Long lease on the decode
					// node so the slot is released when the stream completes
					// (NotifyStreamComplete → _warmLeases) instead of being
					// stranded — a stranded slot 503/empties the NEXT turn
					// (the stream_done_no_lease symptom class).
					await ReassertDecodeLeaseAsync(item, w);
				}
			}

			if (!mergedDecodeOk)
			{
			// HTTP streaming for chat completions (works for both engine and legacy modes).
			// The engine RPC (EngineDecodeStreamAsync) was previously used here in engine
			// mode, but the RPC payload is just raw bytes — it collapsed the model's
			// `reasoning_content` into `content` and dropped `finish_reason`/`id_slot`/
			// `timings`, making the response unusable for reasoning models like
			// Qwopus3.6-35B-A3B (--reasoning on). The HTTP proxy preserves the full
			// OpenAI schema including `reasoning_content`. The engine RPC is still used
			// for prefill (EnginePrefill) and KV state (StateGet/Put). See issue #273.
			// Ask llama-server to emit a final usage chunk so token counts are
			// available on streamed requests (OpenAI omits usage from streams by default).
			item.Request["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
			IAsyncEnumerable<byte[]> streamTask = _proxy.ProxyCompletionStreamAsync(
				w.LlamaUrl, item.Request, item.TraceId, cts.Token);

			item.DecodeChunks = TrackStreamNPast(streamTask, item);
			// Defer BgSave until stream completes — slot is still processing now.
			// Set before StreamCompletion to avoid race: a fast stream could finish
			// and call NotifyStreamComplete before this line runs, orphaning the save.
			_pendingBgSaves[item.SessionId] = (w.Name, item.DecodeSlot ?? 0, item.TraceId);
			item.StreamCompletion.TrySetResult(item.DecodeChunks);
			item.Response = new { streamed = true };
			// Register session in ledger so /status can find it (cold_atomic streaming
			// skips RestoreKvAsync which would have registered; n_past will be updated
			// by TrackStreamNPast as the stream emits usage chunks).
			if (_ledger.Lookup(item.SessionId) == null)
				_ledger.Register(item.SessionId, w.Name, item.DecodeSlot, item.NPastAfter, item.PrefixHash);
			return WorkItemState.Done;
			}
			return WorkItemState.Done;
		}
		else
		{
			// Phase 2b: emit 0x40 EngineConfigure with per-request T1 overrides
			// before activating the peer. T1 keys apply immediately; best-effort
			// (failure is logged, request continues with engine's current config).
			if (_cfg.UseLlamaEngine && item.RequestOverrides is { IsEmpty: false })
				await ApplyRequestOverridesAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, ct);

			// Two-engine "work together": activate the peer before decode (no-op when solo).
			if (_cfg.UseLlamaEngine)
				await ApplyMultiEngineAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, ct);

			// #470: merged decode path — when the engine advertises merged_decode,
			// send the framed DECODE 0x43 with kv_metadata + model_metadata + prompt
			// to get the decode_request_id and model identity match. On success,
			// poll GET /v1/decode/{id} for the synchronous result (skips HTTP proxy).
			bool mergedDecodeOk = false;
			if (decodeNodeMergedCapable)
			{
				bool gateRejected = false;
				try
				{
					// #598: resolution shared with the streaming path —
					// prompt segment, n_predict budget, STATE_META model
					// identity (Gate A) and GGUF-file alias.
					var (messagesJson, nPredict, modelIdentity, modelAlias) =
						await ResolveMergedDecodeRequestAsync(item, w, ct);

					// #470/A7: kv_metadata (what built the KV) comes from the
					// PREFILL response — not queryable on the decode node.
					var kvIdentity = item.GetKvModelIdentity();

					var llamaRpc = GetLlamaRpcClient(w);
					// #470 BUSY-retry (2026-08-13) — non-streaming twin of the
					// streaming path: retry bounded (3 attempts, 500ms/1s/2s)
					// when the engine slot is busy (Status 0x04) instead of
					// letting the gate below turn a transient Busy into a 503.
					MergedDecodeResponse? mergedResp = null;
					var busyAttempt = 0;
					const int busyMaxAttempts = 3;
					while (busyAttempt < busyMaxAttempts)
					{
						if (busyAttempt > 0)
						{
							var backoffMs = busyAttempt switch { 1 => 500, 2 => 1000, _ => 2000 };
							_log.Warning("merged_decode_busy_retry_nonstream Sid={Sid} Node={Node} Attempt={A}/{Max} — slot busy, retrying in {Ms}ms",
								item.SessionId, w.Name, busyAttempt + 1, busyMaxAttempts, backoffMs);
							await Task.Delay(backoffMs, ct);
						}
						if (item.RelayChannel != null)
						{
							// #470 Increment 2 (relay): KV streams live from the
							// PREFILL RPC — no Store read. Consumed ONCE: a BUSY
							// reject cannot be retried, break out immediately.
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only.
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeStreamKvAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								samplingJson: null,
								stream: false,
								kvChunks: RelayKvChunksAsync(item.RelayChannel, ct),
								kvTotalSize: item.KvTotalSize,
								kvHash: item.KvHash,
								traceId: item.TraceId,
								ct: ct);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
							break;
						}
						if (item.KvChunks is { Count: > 0 })
						{
							// #470 Phase 2: KV streamed from the Store (no full
							// blob in RAM). The enumerable re-fetches the chunks
							// on every BUSY retry attempt.
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only.
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeStreamKvAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								// #576: sampling/stop now travel inside messagesJson
								// (the prompt segment), so the separate samplingJson
								// channel stays empty — the engine's generation-header
								// merge only fills keys the prompt object lacks.
								samplingJson: null,
								stream: false,
								kvChunks: StreamKvChunksFromStoreAsync(
									$"{item.SessionId}.kv", item.KvChunks, item.TraceId, ct),
								kvTotalSize: item.KvTotalSize,
								kvHash: item.KvHash,
								traceId: item.TraceId,
								ct: ct);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
						}
						else
						{
							// #620 Task 2/3: time the actual 0x43 DECODE RPC engine call only.
							var decodeRpcSw = System.Diagnostics.Stopwatch.StartNew();
							mergedResp = await llamaRpc.EngineMergedDecodeAsync(
								slotKey: (item.DecodeSlot ?? item.LastIdSlot ?? 0).ToString(),
								nPast: item.NPastAfter,
								kvTokenizer: kvIdentity.Tokenizer,
								kvModelName: kvIdentity.ModelName,
								kvModelQuant: kvIdentity.ModelQuant,
								kvModelCapabilities: kvIdentity.ModelCapabilities,
								modelTokenizer: modelIdentity.Tokenizer,
								modelName: modelIdentity.ModelName,
								modelQuant: modelIdentity.ModelQuant,
								modelCapabilities: modelIdentity.ModelCapabilities,
								modelAlias: modelAlias,
								messagesJson: messagesJson,
								nPredict: nPredict,
								// #576: sampling/stop now travel inside messagesJson
								// (the prompt segment), so the separate samplingJson
								// channel stays empty — the engine's generation-header
								// merge only fills keys the prompt object lacks.
								samplingJson: null,
								stream: false,
								kvBlob: item.KvBlob ?? ReadOnlyMemory<byte>.Empty,
								traceId: item.TraceId,
								ct: ct);
							CoordinatorMetrics.DecodeRpcDuration.WithLabels(w.Name).Observe(decodeRpcSw.Elapsed.TotalSeconds);
						}
						if (mergedResp.Status != (byte)StatusCode.Busy)
							break; // Ok or terminal reject — no more retries
						busyAttempt++;
					}

					// #470 Increment 2 (relay): await the background prefill RPC
					// so a stream failure surfaces with its own error.
					if (item.RelayTask != null)
						await item.RelayTask;

					item.DecodeRequestId = mergedResp.DecodeRequestId;
					CoordinatorMetrics.DecodeRequestIdsIssued.Inc();
					item.Match = new DecodeMatch(
						mergedResp.TokenizerMatch,
						mergedResp.ModelNameMatch,
						mergedResp.ModelCapabilitiesMatch,
						mergedResp.CapabilitiesXor,
						mergedResp.ModelQuantMatch,
						mergedResp.ModelAliasMatch);

					_log.Information("merged_decode_initiated Sid={Sid} DecodeId={Did} Valid={V} NPast={N} RestoreMs={R} InitMs={I} Match=Tok={TM}Name={NM}Cap={CM}Quant={QM}Alias={AM}",
						item.SessionId, mergedResp.DecodeRequestId, mergedResp.Valid,
						mergedResp.NPastAfterRestore, mergedResp.RestoreSlotMs, mergedResp.DecodeInitMs,
						mergedResp.TokenizerMatch, mergedResp.ModelNameMatch,
						mergedResp.ModelCapabilitiesMatch, mergedResp.ModelQuantMatch,
						mergedResp.ModelAliasMatch);
					CoordinatorMetrics.RestoreSlotMs.WithLabels(w.Name).Observe(mergedResp.RestoreSlotMs);
					CoordinatorMetrics.DecodeInitMs.WithLabels(w.Name).Observe(mergedResp.DecodeInitMs);

					if (mergedResp.Valid && mergedResp.DecodeRequestId > 0)
					{
						// #470: Poll GET /v1/decode/{id} for the buffered result.
						var mergedResult = await _proxy.PollDecodeResultAsync(
							w.LlamaUrl, mergedResp.DecodeRequestId!.Value, item.TraceId, ct);
						if (mergedResult.TryGetValue("id_slot", out var s) && s is JsonElement se)
							item.LastIdSlot = se.GetInt32();
						if (item.MultiMode != MultiEngineMode.None)
							mergedResult["hydra"] = MultiEngineStatus(item);

						// #616/#642/#588: a merged result with tokens but NEITHER
						// content NOR reasoning_content NOR tool_calls (all three now
						// delivered in the DONE result, engine 097d13e/b95c228b; a
						// reasoning- or tool-call-only reply must NOT be re-issued)
						// is re-issued ONCE via the HTTP proxy with the CLEAN client
						// body (snapshot before id_slot / hydra_config injection).
						// Bounded to a single attempt — the HTTP path never
						// re-enters merged decode, so there is no loop.
						if (MergedDecodeResultHasEmptyContent(mergedResult, out var emptyTokens))
						{
							_log.Warning("merged_decode_empty_content_fallback sid={Sid} tokens={N}",
								item.SessionId, emptyTokens);
							mergedResult = await _proxy.ProxyCompletionAsync(
								w.LlamaUrl, cleanRequestBody!, item.TraceId, ct);
							if (mergedResult.TryGetValue("id_slot", out var s2) && s2 is JsonElement se2)
								item.LastIdSlot = se2.GetInt32();
							if (item.MultiMode != MultiEngineMode.None)
								mergedResult["hydra"] = MultiEngineStatus(item);
						}

						item.Response = mergedResult;
						item.TokensIn = ExtractUsageInt(mergedResult, "prompt_tokens");
						item.TokensOut = ExtractUsageInt(mergedResult, "completion_tokens");
						if (mergedResult.TryGetValue("hydra_metrics", out var hm) && hm is JsonElement hmEl && hmEl.ValueKind == JsonValueKind.Object)
						{
							_log.Information("hydra_metrics Sid={Sid} Head={Head} Reloaded={Reloaded} ReloadMs={Ms}",
								item.SessionId, w.Name,
								hmEl.TryGetProperty("t3_reloaded", out var tr) && tr.GetBoolean(),
								hmEl.TryGetProperty("t3_reload_ms", out var rm) ? rm.GetDouble() : 0);
							// #470 non-streaming: PrefillAsync was skipped for this route
							// (engine did inline prefill+decode over 0x43 DECODE), so
							// prefill_ms was never recorded. Backfill from the engine's
							// own prompt_ms — mirrors the streaming hydra_metrics
							// extraction (see the SSE loop below, ~prompt_ms/decode_ms).
							if ((!item.Phases.ContainsKey("prefill_ms") || item.Phases["prefill_ms"] == 0)
								&& hmEl.TryGetProperty("prompt_ms", out var prm) && prm.ValueKind == JsonValueKind.Number)
							{
								item.EnginePrefillMs = (long)prm.GetDouble();
								item.Phases["prefill_ms"] = item.EnginePrefillMs;
							}
							// #620 Task 3/3a: engine-reported decode_ms from the merged
							// non-streaming result's hydra_metrics (authoritative engine field).
							if (hmEl.TryGetProperty("decode_ms", out var dm) && dm.ValueKind == JsonValueKind.Number)
							{
								item.Phases["engine_decode_ms"] = (long)dm.GetDouble();
								CoordinatorMetrics.EngineDecodeMs.WithLabels(w.Name)
									.Observe(item.Phases["engine_decode_ms"]);
							}
						}
						if (_ledger.Lookup(item.SessionId) == null)
							_ledger.Register(item.SessionId, w.Name,
								item.LastIdSlot ?? 0, item.NPastAfter, item.PrefixHash);
						TrackAfterCompletion(item.SessionId, mergedResult);
						mergedDecodeOk = true;
					}
					else
					{
						// #470: Enforcing gate — see streaming path comment.
						gateRejected = true;
						throw new InvalidOperationException(
							$"DECODE 0x43 rejected Sid={item.SessionId} Valid={mergedResp.Valid} DecodeId={mergedResp.DecodeRequestId} — KV not restored, aborting");
					}
				}
				catch (Exception ex) when (!gateRejected && item.RelayChannel == null)
				{
					// #470 Increment 2 (relay): no HTTP-proxy fallback — the KV
					// stream was consumed once and cannot be replayed; a fault
					// fails the turn with the root error (no-fallback rule).
					_log.Warning(ex, "merged_decode_transport_fault Sid={Sid} — falling back to HTTP proxy",
						item.SessionId);
					// #470 Fix 3: same lease re-assertion as the streaming
					// path — the non-streaming reply is assembled from the
					// HTTP-proxy result below regardless of lease state, but
					// the slot must still be held (and later released via the
					// warm-lease path) so the next turn is not starved.
					await ReassertDecodeLeaseAsync(item, w);
				}
			}

			if (!mergedDecodeOk)
			{
				// HTTP proxy for chat completions (works for both engine and legacy modes).
				// The engine RPC (EngineDecodeAsync) was previously used here in engine
				// mode, but the RPC payload is just raw bytes — it collapsed the model's
				// `reasoning_content` into `content` and dropped `finish_reason`/`id_slot`/
				// `timings`, making the response unusable for reasoning models like
				// Qwopus3.6-35B-A3B (--reasoning on). The HTTP proxy preserves the full
				// OpenAI schema including `reasoning_content`. The engine RPC is still used
				// for prefill (EnginePrefill) and KV state (StateGet/Put). See issue #273.
				using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(item.HttpCancellationToken, ct);
				// #469 trace: log decode request body for cross-flow comparison
				if (item.Request.TryGetValue("messages", out var msgs) && msgs is JsonElement decodeMsgEl && decodeMsgEl.ValueKind == JsonValueKind.Array)
				{
					var decodeMsgCount = decodeMsgEl.GetArrayLength();
					var decodeFirstMsg = decodeMsgCount > 0 ? decodeMsgEl[0].ToString()[..Math.Min(80, decodeMsgEl[0].ToString().Length)] : "?";
					var decodeLastMsg = decodeMsgCount > 0 ? decodeMsgEl[decodeMsgCount - 1].ToString()[..Math.Min(80, decodeMsgEl[decodeMsgCount - 1].ToString().Length)] : "?";
					_log.Debug("#PD-TRACE DECODE_REQUEST Sid={Sid} MsgCount={Count} FirstMsg={First} LastMsg={Last} Slot={Slot} NPast={NPast}",
						item.SessionId, decodeMsgCount, decodeFirstMsg, decodeLastMsg, item.DecodeSlot, item.NPastAfter);
				}
				// #479/S3 + #504: translate the routing-identity model on the decode
				// request to the GGUF-file alias so the engine's inline reload fires
				// instead of silently falling back to its resident model.
				// For model-agnostic workers (e.g. RTX), w.ModelAlias is null —
				// the canonical identity's DECODE alias is the source (#470).
				// Body-level substitution: the proxy body copy gets the translated
				// alias; Request["model"] itself stays frozen as the raw routing
				// key (never mutated in place).
				var proxyBody = new Dictionary<string, object>(item.Request);
				if (_cfg.UseLlamaEngine)
				{
					var decodeAlias = !string.IsNullOrEmpty(w.ModelAlias)
						? TranslateModelAlias(w.ModelAlias, decodeRole: true)
						: item.ModelIdentity?.DecodeAlias ?? TranslateModelAlias(RequestModelString(item), decodeRole: true);
					if (!string.IsNullOrEmpty(decodeAlias))
						proxyBody["model"] = decodeAlias;
				}
				var resp = await _proxy.ProxyCompletionAsync(
						w.LlamaUrl, proxyBody, item.TraceId, syncCts.Token);
				if (resp.TryGetValue("id_slot", out var s2) && s2 is JsonElement se2)
					item.LastIdSlot = se2.GetInt32();
				if (item.MultiMode != MultiEngineMode.None)
					resp["hydra"] = MultiEngineStatus(item);
				item.Response = resp;
				item.TokensIn = ExtractUsageInt(resp, "prompt_tokens");
				item.TokensOut = ExtractUsageInt(resp, "completion_tokens");

				if (resp.TryGetValue("hydra_metrics", out var hm) && hm is JsonElement hmEl && hmEl.ValueKind == JsonValueKind.Object)
				{
					_log.Information("hydra_metrics Sid={Sid} Head={Head} Reloaded={Reloaded} ReloadMs={Ms}",
						item.SessionId, w.Name,
						hmEl.TryGetProperty("t3_reloaded", out var tr) && tr.GetBoolean(),
						hmEl.TryGetProperty("t3_reload_ms", out var rm) ? rm.GetDouble() : 0);
					// #470 non-streaming: PrefillAsync was skipped for this route
					// (engine did inline prefill+decode over 0x43 DECODE), so
					// prefill_ms was never recorded. Backfill from the engine's
					// own prompt_ms — mirrors the streaming hydra_metrics
					// extraction (see the SSE loop below, ~prompt_ms/decode_ms).
					if ((!item.Phases.ContainsKey("prefill_ms") || item.Phases["prefill_ms"] == 0)
						&& hmEl.TryGetProperty("prompt_ms", out var prm) && prm.ValueKind == JsonValueKind.Number)
					{
						item.EnginePrefillMs = (long)prm.GetDouble();
						item.Phases["prefill_ms"] = item.EnginePrefillMs;
					}
					// #620 Task 3/3a: engine-reported decode_ms from the HTTP-proxy
					// non-streaming result's hydra_metrics (authoritative engine field).
					if (hmEl.TryGetProperty("decode_ms", out var dm) && dm.ValueKind == JsonValueKind.Number)
					{
						item.Phases["engine_decode_ms"] = (long)dm.GetDouble();
						CoordinatorMetrics.EngineDecodeMs.WithLabels(w.Name)
							.Observe(item.Phases["engine_decode_ms"]);
					}
				}

				// Register in ledger so /status can find the session. The cold_atomic HTTP
				// path skips RestoreKvAsync (which would have registered in the P/D split
				// path). The previous engine path registered inline; the HTTP path never
				// did and sessions went missing from /status. Register first so the
				// TrackAfterCompletion call below can update NPast on the live entry.
				if (_ledger.Lookup(item.SessionId) == null)
					_ledger.Register(item.SessionId, w.Name,
						item.LastIdSlot ?? 0, item.NPastAfter, item.PrefixHash);

				// Track n_past from completion response
				TrackAfterCompletion(item.SessionId, resp);
			}
		}
		SplitInlinePrefillFromDecode(item, item.RecordPhase("decode_ms"));
		CoordinatorMetrics.DecodeDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.Phases.GetValueOrDefault("decode_ms") / 1000.0);
		// #620 Task 3/3b: engine decode_ms − coordinator-measured decode_ms.
		// Positive = engine claims more time than the coordinator observed.
		// Observe only when both sources are present (engine value parsed from
		// hydra_metrics at the 3a site; coordinator decode_ms finalized above).
		if (item.Phases.TryGetValue("engine_decode_ms", out var engineDecodeMs)
			&& item.Phases.TryGetValue("decode_ms", out var coordDecodeMs))
			CoordinatorMetrics.EngineVsCoordinatorDecodeMs.WithLabels(w.Name)
				.Observe(engineDecodeMs - coordDecodeMs);
		// #470: merged-decode route skipped PrefillAsync (which normally
		// observes PrefillDuration itself, :2088) — EnginePrefillMs > 0 is
		// the marker that SplitInlinePrefillFromDecode backfilled prefill_ms
		// from the engine's own hydra_metrics.prompt_ms, so observe it here
		// instead of leaving hydra_prefill_seconds silently unrecorded.
		if (item.EnginePrefillMs > 0)
			CoordinatorMetrics.PrefillDuration.WithLabels(w.Name, RouteLabel(item))
				.Observe(item.Phases.GetValueOrDefault("prefill_ms") / 1000.0);
		return WorkItemState.BgSave;
	}

	private async Task<WorkItemState> BgSaveAsync(WorkItem item)
	{
		// Issue #277: BgSave was previously fire-and-forget (`_ = Task.Run(...)`).
		// The race: a new decode can TryAcquireSlot the same slot and start its chat
		// completion while the previous turn's StateGet RPC is still in flight on
		// llama-server, which serializes per-slot — the new decode hangs for the
		// 30s HTTP timeout. Fix: await the bg_save synchronously so it completes
		// before BgSaveAsync returns. The state machine blocks for the (typically
		// sub-second) bg_save, but the response is already sent to the client so
		// the user sees no extra latency. Only the next queued item is delayed.
		var w = item.DecodeWorker!;

		try
		{
			// #635 fix 4: ALWAYS pull the current slot state via StateGet. The
			// old engine-mode shortcut wrote item.KvBlob, which on merged-decode
			// routes is the PRE-decode restore blob (RestoreKvAsync sets it) —
			// persisting that regressed the stored KV to the pre-decode state.
			// StateGet returns the true post-decode state, and PersistKvToStoreAsync
			// keeps the chunk manifest in sync with it.
			var slotId = item.LastIdSlot ?? item.DecodeSlot ?? 0;
			var llamaRpc = GetStateRpcClient(w);
			var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
				slotId.ToString(), ReadOnlyMemory<byte>.Empty, item.TraceId, CancellationToken.None);

			if (stateResp.Status == (byte)Hydra.Shared.StatusCode.Ok)
			{
				await PersistKvToStoreAsync(item.SessionId, stateResp.Payload, item, item.TraceId, CancellationToken.None);
				_ledger.MarkStoreState(item.SessionId);
				_log.Information("bg_saved Sid={Sid} Slot={Slot} bytes={Bytes} (engine state, post-decode)",
					item.SessionId, slotId, stateResp.Payload.Length);
			}
			else
			{
				_log.Warning("bg_save_busy Sid={Sid} Slot={Slot} Status={Status}",
					item.SessionId, slotId, stateResp.Status);
			}
		}
		catch (Exception ex) { _log.Error(ex, "bg_save_failed"); }

		return WorkItemState.Done;
	}

	// ── Warm lease eviction ──

	public async Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct)
	{
		if (!_warmLeases.TryRemove(sessionId, out var lease))
		{
			_ledger.MarkEvicted(sessionId);
			return;
		}

		// Save KV before erasing the slot — the GPU data is still live
		await SaveSlotStateBeforeEvictAsync(sessionId, nodeName, lease.SlotId, "evict-api", ct);

		try
		{
			var w = _cfg.Workers.FirstOrDefault(x => x.Name == nodeName);
			if (w != null)
			{
				var llama = GetLlamaClient(w);
				await llama.EraseSlotAsync(lease.SlotId, ct);
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "warm_evict_erase_failed Sid={Sid} Slot={Slot}",
				sessionId, lease.SlotId);
		}
		finally
		{
			await lease.DisposeAsync();
		}

		_ledger.MarkEvicted(sessionId);
		_log.Information("warm_session_evicted Sid={Sid} Node={Node} Slot={Slot}",
			sessionId, nodeName, lease.SlotId);
	}

	public async Task NotifyStreamComplete(string sessionId)
	{
		// Issue #284 + #286: two related bugs fixed together.
		//
		// 1) Lease release was at the END of NotifyStreamComplete (in the finally
		//    block), AFTER the bg-save's StateGet + Store Put completed. For
		//    14K-20K-token warm sessions, the bg-save is ~100-200 MB; the
		//    RPC + disk write could take 10-60s. The slot was held the whole
		//    time, starving the pool under opencode-style concurrent load.
		//
		// 2) An exception in the early steps (TryAdd / TryRemove / EmitTimeline)
		//    could skip the finally, leaking the slot forever.
		//
		// Fix: capture the state blob via StateGet, then release the slot
		// IMMEDIATELY, then write to Store. The blob is in our memory
		// (stateResp.Payload is a fresh byte[] from ReadPayloadAsync) so the
		// Put no longer needs the engine slot. The Put is fire-and-forget
		// below: NotifyStreamComplete returns as soon as the slot is free.
		// The defensive finally in #285 stays in place in case the early
		// steps throw before we reach the in-block slot release.
		var releaseStart = System.Diagnostics.Stopwatch.StartNew();
		string? releaseNode = null;
		try
		{
			// Search _pendingTimelines (keyed by TraceId) for an entry matching
			// this session whose stream has ACTUALLY completed. When two streaming
			// requests share the same sessionId, this prevents NotifyStreamComplete
			// from removing the wrong entry (the other request's still-streaming item).
			WorkItem? timelineItem = null;
			foreach (var kv in _pendingTimelines)
			{
				if (kv.Value.SessionId == sessionId
					&& kv.Value.StreamCompletion.Task.IsCompleted
					&& _pendingTimelines.TryRemove(kv.Key, out timelineItem))
					break;
			}

			_pendingBgSaves.TryGetValue(sessionId, out var bgInfo);
			var traceId = bgInfo.TraceId;

			// Key _streamCompleted by TraceId (per-turn) to avoid stale entries
			// from failed requests leaking into subsequent turns for the same session.
			if (traceId is { Length: > 0 })
				_streamCompleted.TryAdd(traceId, 0);

			// Emit the deferred timeline now that the stream is done — decode_ms/total_ms
			// cover the full stream duration.
			if (timelineItem != null)
			{
				FinalizeStreamPhases(timelineItem);
				CoordinatorMetrics.DecodeDuration
					.WithLabels(timelineItem.DecodeWorker?.Name ?? "unknown", RouteLabel(timelineItem))
					.Observe(timelineItem.Phases.GetValueOrDefault("decode_ms") / 1000.0);
				// #620 Task 3/3b: engine decode_ms − coordinator-measured decode_ms.
				// Positive = engine claims more time than the coordinator observed.
				// Observe only when both sources are present (engine value parsed
				// from hydra_metrics during the stream; coordinator decode_ms
				// finalized by FinalizeStreamPhases above).
				if (timelineItem.Phases.TryGetValue("engine_decode_ms", out var engineDecodeMs)
					&& timelineItem.Phases.TryGetValue("decode_ms", out var coordDecodeMs))
					CoordinatorMetrics.EngineVsCoordinatorDecodeMs
						.WithLabels(timelineItem.DecodeWorker?.Name ?? "unknown")
						.Observe(engineDecodeMs - coordDecodeMs);
				// #470: merged-decode route skipped PrefillAsync (which normally
				// observes PrefillDuration itself, :2088) — EnginePrefillMs > 0
				// is the marker that FinalizeStreamPhases backfilled prefill_ms
				// from the engine's own hydra_metrics.prompt_ms, so observe it
				// here instead of leaving hydra_prefill_seconds silently unrecorded.
				if (timelineItem.EnginePrefillMs > 0)
					CoordinatorMetrics.PrefillDuration
						.WithLabels(timelineItem.DecodeWorker?.Name ?? "unknown", RouteLabel(timelineItem))
						.Observe(timelineItem.Phases.GetValueOrDefault("prefill_ms") / 1000.0);
				EmitTimeline(timelineItem);
			}

			// Dispose the pipeline cancellation token source (linked from HTTP ct + scheduler ct)
			if (_pipelineCts.TryRemove(sessionId, out var pipelineCts))
				pipelineCts.Dispose();

			// Capture the KV blob from the engine. This RPC must hold the slot
			// (it reads from the engine's slot buffer), but it's a single round
			// trip that returns a fresh byte[] in our memory.
			Hydra.Shared.RpcResponse? stateResp = null;
			string? bgTraceId = null;
			var bgRpcSw = System.Diagnostics.Stopwatch.StartNew();
			if (_cfg.NoStoreKvRestore)
			{
				// NoStoreKvRestore: skip StateGet entirely — no KV to persist.
				// Release slot immediately without the 8-10s StateGet RPC.
				if (_pendingBgSaves.TryRemove(sessionId, out var bgInfoSkip))
				{
					var wSkip = _cfg.Workers.FirstOrDefault(x => x.Name == bgInfoSkip.WorkerName);
					if (wSkip != null) releaseNode = wSkip.Name;
					bgTraceId = bgInfoSkip.TraceId;
				}
			}
			else if (_pendingBgSaves.TryRemove(sessionId, out var bgInfo2))
			{
				var w = _cfg.Workers.FirstOrDefault(x => x.Name == bgInfo2.WorkerName);
				if (w != null)
				{
					releaseNode = w.Name;
					bgTraceId = bgInfo2.TraceId;
					try
					{
						var llamaRpc = GetStateRpcClient(w);
						stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
							bgInfo2.SlotId.ToString(), ReadOnlyMemory<byte>.Empty, bgInfo2.TraceId, CancellationToken.None);
					}
					catch (Exception ex) { _log.Error(ex, "bg_state_get_failed"); }
				}
			}

			// ★ Release slot NOW — the blob is in our memory (or StateGet failed,
			// in which case there's no blob to save). The Put below does not need
			// the slot. This is the key change for #286: slot-release lag drops
			// from 10-60s to <100ms.
			if (_warmLeases.TryRemove(sessionId, out var lease))
			{
				_log.Information("stream_done_release Sid={Sid} Worker={W} Slot={Slot}",
					sessionId, lease.WorkerName, lease.SlotId);
				if (releaseNode is null) releaseNode = lease.WorkerName;
				try { await lease.DisposeAsync(); }
				catch (Exception ex) { _log.Error(ex, "lease_dispose_failed Sid={Sid}", sessionId); }
				SignalEvaluator();
			}
			else
			{
				_log.Warning("stream_done_no_lease Sid={Sid} WarmKeys={Keys}",
					sessionId, string.Join(",", _warmLeases.Keys.Take(5)));
				// #470 Fix 3: the lease must not be lost. When the warm-lease
				// stash is empty for this session, the item's own DecodeLease
				// may still be held (e.g. the pipeline finalized outside the
				// warm-lease stash path after a merged_decode_transport_fault).
				// Release it here so the slot returns to the pool instead of
				// being stranded — a stranded slot 503/empties the NEXT turn
				// of the same session. The streamed reply is unaffected: it is
				// assembled from DecodeChunks and does not depend on the lease.
				foreach (var kv in _pendingTimelines)
				{
					if (kv.Value.SessionId != sessionId || kv.Value.DecodeLease == null)
						continue;
					var orphanedLease = kv.Value.DecodeLease;
					kv.Value.DecodeLease = null;
					if (releaseNode is null) releaseNode = orphanedLease.WorkerName;
					try { await orphanedLease.DisposeAsync(); }
					catch (Exception ex) { _log.Error(ex, "lease_dispose_failed Sid={Sid}", sessionId); }
					SignalEvaluator();
					break;
				}
			}

			// Release the peer lease (two-engine) once the stream is fully drained.
			// A4: await the release with its own try/catch instead of fire-and-forget —
			// a swallowed dispose exception previously leaked the peer slot silently.
			if (_peerLeases.TryRemove(sessionId, out var peerLease))
			{
				try { await peerLease.DisposeAsync(); }
				catch (Exception ex) { _log.Error(ex, "peer_lease_dispose_failed Sid={Sid}", sessionId); }
				if (_activeMultiSessions.TryRemove(sessionId, out var modeStr))
					CoordinatorMetrics.MultiEngineActiveSessions.WithLabels(modeStr).Dec();
			}

			// Fire-and-forget the Store Put. The slot is already free; this
			// write no longer blocks the next request. If the process dies
			// before the Put completes, the state is lost (same as before,
			// since the old code also Put in this same task — just with the
			// slot held the whole time). Failures are logged, not raised.
			if (stateResp is { Status: (byte)Hydra.Shared.StatusCode.Ok })
			{
				_ = WriteStateToStoreAsync(stateResp.Payload, sessionId, bgTraceId ?? "",
					timelineItem, bgRpcSw.ElapsedMilliseconds);
			}
			else if (stateResp is { Status: not (byte)Hydra.Shared.StatusCode.Ok })
			{
				_log.Warning("bg_save_busy Sid={Sid} Status={Status}", sessionId, stateResp.Status);
			}
		}
		catch (Exception ex)
		{
			// Issue #284: a non-fatal error in the early steps must not leak the slot.
			// Log + count, then fall through to lease release in finally.
			_log.Error(ex, "stream_complete_early_error Sid={Sid}", sessionId);
			CoordinatorMetrics.SlotReleaseErrors.Inc();
		}
		finally
		{
			// Defensive recovery: if the try block threw before reaching the
			// in-block lease release, release here. TryRemove is idempotent,
			// so a double release is impossible.
			if (_warmLeases.TryRemove(sessionId, out var lease))
			{
				_log.Information("stream_done_release_recovery Sid={Sid} Worker={W} Slot={Slot}",
					sessionId, lease.WorkerName, lease.SlotId);
				if (releaseNode is null) releaseNode = lease.WorkerName;
				try { await lease.DisposeAsync(); }
				catch (Exception ex) { _log.Error(ex, "lease_dispose_failed Sid={Sid}", sessionId); }
				SignalEvaluator();
			}

			// Issue #284: record the time the slot was held after the request ended.
			releaseStart.Stop();
			CoordinatorMetrics.SlotReleaseLag
				.WithLabels(releaseNode ?? "unknown")
				.Observe(releaseStart.Elapsed.TotalSeconds);
		}
	}

	// Fire-and-forget disk write. Runs in a separate task so NotifyStreamComplete
	// can return as soon as the slot is released. Failures are logged.
	private async Task WriteStateToStoreAsync(byte[] stateBlob, string sessionId, string traceId,
		WorkItem? timelineItem, long bgRpcMs)
	{
		var saveStart = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			// #635 fix 4: persist the post-decode state AND (in chunk mode) keep
			// the chunk manifest in sync so a migration continuation's restore
			// reads the latest blob instead of the stale pre-decode manifest.
			await PersistKvToStoreAsync(sessionId, stateBlob, timelineItem, traceId, CancellationToken.None);
			_ledger.MarkStoreState(sessionId);
			var storeMs = saveStart.ElapsedMilliseconds;
			_log.Information("bg_saved Sid={Sid} bytes={Bytes} rpc_ms={RpcMs} store_ms={StoreMs} total_ms={Total}",
				sessionId, stateBlob.Length, bgRpcMs, storeMs, bgRpcMs + storeMs);
			// Emit an updated timeline with save_kv_rpc_ms and save_kv_store_ms
			// so Grafana shows the KV save phase after the response was sent.
			if (timelineItem != null)
			{
				timelineItem.Phases["save_kv_rpc_ms"] = bgRpcMs;
				timelineItem.Phases["save_kv_store_ms"] = storeMs;
				EmitTimeline(timelineItem, "bg_saved");
			}
		}
		catch (Exception ex)
		{
			_log.Error(ex, "bg_save_async_failed Sid={Sid} bytes={Bytes}",
				sessionId, stateBlob.Length);
			CoordinatorMetrics.SaveKvErrors.Inc();
		}
		finally
		{
			saveStart.Stop();
			CoordinatorMetrics.SaveKvAsyncDuration
				.WithLabels("ok") // could be enriched with success/failure label
				.Observe(saveStart.Elapsed.TotalSeconds);
		}
	}

	public int WarmLeaseCount => _warmLeases.Count;

	public Dictionary<string, SlotLease> GetWarmLeasesSnapshot()
		=> new(_warmLeases);

	private Hydra.Shared.RpcClient? GetAgentByName(string name)
	{
		var w = _cfg.Workers.FirstOrDefault(x => x.Name == name);
		return w != null ? GetAgent(w) : null;
	}

	internal async Task FinalizeAsync(WorkItem item, WorkItemState end)
	{
		item.State = end;

		// Dispose short-lived prefill lease (cold paths)
		if (item.PrefillLease != null)
		{
			await item.PrefillLease.DisposeAsync();
			item.PrefillLease = null;
		}

		// Decode lease: holds slot until streaming completes (Long lifetime).
		// The controller signals NotifyStreamComplete when all SSE chunks are written.
		var streamFinishedEarly = false;
		if (item.DecodeLease != null)
		{
			if (item.DecodeLease.Lifetime == LeaseLifetime.Long
				&& end == WorkItemState.Done)
			{
				// If streaming already completed (short response), release immediately.
				// Otherwise store as warm — NotifyStreamComplete will release it.
				if (_streamCompleted.TryRemove(item.TraceId, out _))
				{
					streamFinishedEarly = true;
					await item.DecodeLease.DisposeAsync();
					SignalEvaluator();
				}
				else
				{
					// Evict any prior warm lease for this session before stashing the new one.
					// A cross-node fallback turn leaves the old node's lease here; without
					// this guard the old slot is never returned to its pool.
					if (_warmLeases.TryRemove(item.SessionId, out var staleLease))
					{
						await staleLease.DisposeAsync();
						SignalEvaluator();
					}
					_warmLeases[item.SessionId] = item.DecodeLease;
				}
			}
			else
			{
				await item.DecodeLease.DisposeAsync();
				SignalEvaluator();
			}

			item.DecodeLease = null;
		}

		// Peer lease (two-engine): for non-streaming or already-finished streams, release now;
		// for an in-flight stream, hand it to NotifyStreamComplete alongside the warm decode lease.
		if (item.PeerLease != null)
		{
			if (item.IsStreaming && end == WorkItemState.Done && !streamFinishedEarly)
				_peerLeases[item.SessionId] = item.PeerLease;
			else
				await ReleasePeerLeaseAsync(item.SessionId, item.PeerLease);
			item.PeerLease = null;
		}

		if (item.IsStreaming && end == WorkItemState.Done)
		{
			if (streamFinishedEarly)
			{
				FinalizeStreamPhases(item);
				EmitTimeline(item);
			}
			else
			{
				// Stream still in flight — NotifyStreamComplete emits the timeline.
				// Key by TraceId (not SessionId) so concurrent requests for the
				// same session don't overwrite each other's deferred timelines.
				_pendingTimelines[item.TraceId] = item;
				// Close the race where the stream completed between the lease check
				// above and the stash: whoever removes the pending entry emits.
				if (_streamCompleted.ContainsKey(item.TraceId)
					&& _pendingTimelines.TryRemove(item.TraceId, out _))
				{
					FinalizeStreamPhases(item);
					EmitTimeline(item);
				}
			}
		}
		else
		{
			item.Phases["total_ms"] = item.ElapsedMs;
			EmitTimeline(item);
		}
		if (item.Completion.Task.IsCompleted) return;
		if (end == WorkItemState.Done)
			item.Completion.TrySetResult(item.Response);
		else if (end == WorkItemState.Cancelled)
			item.Completion.TrySetCanceled();
		else
		{
			var ex = item.Error ?? new InvalidOperationException("Failed");
			item.Completion.TrySetException(ex);
			// Unblock the streaming path: SubmitAsync waits on StreamCompletion
			// with a 600s timeout. Without this, a failed streaming request
			// hangs the HTTP connection for the full 10 minutes.
			if (item.IsStreaming)
				item.StreamCompletion.TrySetException(ex);
		}
	}

	// ── Timeline helpers ──

	private static string RouteLabel(WorkItem item) =>
		string.IsNullOrEmpty(item.RouteType) ? "unknown" : item.RouteType;

	/// <summary>
	/// Split a raw decode-phase duration into prefill_ms/decode_ms when the
	/// engine did an inline prefill (RouteDecision→Decode skipped
	/// PrefillAsync). item.EnginePrefillMs is only set via the hydra_metrics/
	/// timings backfill on that path, so this silently no-ops for classic
	/// P/D-split requests where PrefillAsync already ran and recorded its
	/// own prefill_ms. Shared by the streaming (FinalizeStreamPhases) and
	/// non-streaming (DecodeAsync) completion paths.
	/// </summary>
	private static void SplitInlinePrefillFromDecode(WorkItem item, long rawDecodeMs)
	{
		item.Phases["decode_ms"] = rawDecodeMs;
		// Engine mode: when PrefillAsync was skipped (RouteDecision→Decode),
		// decode_ms includes the engine's inline prefill. Subtract it so the
		// Grafana stacked bars (prefill + decode) sum to ≈ total_ms.
		if (item.EnginePrefillMs > 0 && rawDecodeMs > item.EnginePrefillMs)
		{
			item.Phases["decode_ms"] = rawDecodeMs - item.EnginePrefillMs;
			item.Phases["prefill_ms"] = item.EnginePrefillMs;
		}
	}

	/// <summary>Set decode_ms/total_ms for a streaming item once the stream has finished.</summary>
	private static void FinalizeStreamPhases(WorkItem item)
	{
		var rawDecodeMs = item.ElapsedMs - item.DecodeStartMs;
		SplitInlinePrefillFromDecode(item, rawDecodeMs);
		item.Phases["total_ms"] = item.ElapsedMs;
	}

	/// <summary>Resolve the dashboard-facing display label for a worker node.
	/// Uses the worker config's DisplayName (e.g. "RTX 5060 Ti") when present,
	/// falling back to the raw worker name — so dashboards show friendly GPU
	/// labels sourced from Hydra.Core's workers.json, not hard-coded names.</summary>
	private string NodeDisplayName(string workerName)
		=> _cfg.Workers.FirstOrDefault(w => string.Equals(w.Name, workerName, StringComparison.OrdinalIgnoreCase))
			?.DisplayName ?? workerName;

	/// <summary>Quote a logfmt value that contains whitespace (e.g. a display
	/// name like "RTX 5060 Ti") so Loki's kvp parser keeps it as one field.
	/// Values without whitespace are returned untouched.</summary>
	private static string KvpValue(string v)
		=> v.IndexOfAny([' ', '\t', '\r', '\n']) >= 0 ? $"\"{v}\"" : v;

	/// <summary>
	/// Emit the per-request phase timeline as a raw logfmt stderr line. Grafana's
	/// timeline dashboard parses this line via extractFields — keep keys stable.
	/// Phase values are per-phase durations (WorkItem.RecordPhase), so they sum
	/// to ≈ total_ms and can be rendered as stacked bars.
	/// </summary>
	private void EmitPartialTimeline(WorkItem item, string status)
	{
		var node = item.PrefillWorker?.Name ?? item.DecodeWorker?.Name ?? "unknown";
		var prefillNode = item.PrefillWorker != null ? NodeDisplayName(item.PrefillWorker.Name) : "-";
		var decodeNode = item.DecodeWorker != null ? NodeDisplayName(item.DecodeWorker.Name) : "-";
		var prefillModel = item.PrefillWorker != null
			? (_health.GetNodeInfo(item.PrefillWorker.Name)?.CurrentModel ?? "")
			: "";
		var decodeModel = item.DecodeWorker != null
			? (_health.GetNodeInfo(item.DecodeWorker.Name)?.CurrentModel ?? "")
			: "";
		// M-Perf.10: split save into rpc (engine→core) + store (core→Store)
		// for the dashboard; keep save_kv_ms as the sum for back-compat.
		var saveKvRpcMs = item.Phases.GetValueOrDefault("save_kv_rpc_ms");
		var saveKvStoreMs = item.Phases.GetValueOrDefault("save_kv_store_ms");
		var saveKvMs = saveKvRpcMs + saveKvStoreMs;
		var modelLoadMs = item.Phases.GetValueOrDefault("model_load_ms");
		// Resolved routing alias (AutoRouter's decision / TranslateModelAlias
		// target) — distinct from prefill_model/decode_model (what's actually
		// resident on the engine node). A mismatch is a fallback signal.
		var requestModel = item.Request.GetValueOrDefault("model")?.ToString() ?? "";
		Console.Error.WriteLine(
			$"event=request_timeline timestamp_ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
			$"trace_id={item.TraceId} session_id={item.SessionId} " +
			$"queue_wait_ms={item.Phases.GetValueOrDefault("queue_wait_ms")} node={node} " +
			$"route_type={RouteLabel(item)} " +
			$"prefill_node={KvpValue(prefillNode)} decode_node={KvpValue(decodeNode)} " +
			$"prefill_model={prefillModel} decode_model={decodeModel} request_model={requestModel} " +
			$"prefill_ms={item.Phases.GetValueOrDefault("prefill_ms")} " +
			$"model_load_ms={modelLoadMs} " +
			$"save_kv_ms={saveKvMs} " +
			$"save_kv_rpc_ms={saveKvRpcMs} " +
			$"save_kv_store_ms={saveKvStoreMs} " +
			$"restore_kv_ms={item.Phases.GetValueOrDefault("restore_kv_ms")} " +
			$"decode_ms={item.Phases.GetValueOrDefault("decode_ms")} " +
			$"tokens_in={item.TokensIn} tokens_out={item.TokensOut} kv_bytes={item.KvBytes} " +
			$"prefix_hit={(item.PrefixCacheHit ? "true" : "false")} " +
			$"status={status}"
		);
		Log.Information("event=request_timeline timestamp_ms={TimestampMs} trace_id={TraceId} session_id={SessionId} queue_wait_ms={QueueWaitMs} node={Node} route_type={RouteType} prefill_node={PrefillNode} decode_node={DecodeNode} prefill_model={PrefillModel} decode_model={DecodeModel} request_model={RequestModel} prefill_ms={PrefillMs} model_load_ms={ModelLoadMs} save_kv_ms={SaveKvMs} save_kv_rpc_ms={SaveKvRpcMs} save_kv_store_ms={SaveKvStoreMs} restore_kv_ms={RestoreKvMs} decode_ms={DecodeMs} tokens_in={TokensIn} tokens_out={TokensOut} kv_bytes={KvBytes} prefix_hit={PrefixHit} status={Status}",
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			item.TraceId, item.SessionId,
			item.Phases.GetValueOrDefault("queue_wait_ms"), node,
			RouteLabel(item),
			KvpValue(prefillNode), KvpValue(decodeNode),
			prefillModel, decodeModel, requestModel,
			item.Phases.GetValueOrDefault("prefill_ms"), modelLoadMs, saveKvMs,
			saveKvRpcMs, saveKvStoreMs,
			item.Phases.GetValueOrDefault("restore_kv_ms"),
			item.Phases.GetValueOrDefault("decode_ms"),
			item.TokensIn, item.TokensOut, item.KvBytes,
			item.PrefixCacheHit ? "true" : "false", status);
	}

	/// <summary>Calculate workload-aware BUSY timeouts based on estimated token count.</summary>
	/// <param name="estimatedTokens">Prompt token count from the request. Falls back to 10K if 0.</param>
	/// <returns>(stuckTimeout, slowTimeout) in milliseconds.</returns>
	internal static (long stuckMs, long slowMs) CalculateBusyTimeouts(long estimatedTokens)
		=> CalculateBusyTimeouts(estimatedTokens, modelLoadTimeS: 0);

	/// <summary>
	/// #635 fix 2: delay before the next prefill RPC retry. The old instant
	/// ~100ms retries burned the whole budget in ~4s — sized for transient
	/// errors, not a crashed engine, and with no wait-for-restart. The backoff
	/// table (500ms/2s/8s) spreads 3 retries over ~10.5s; when the worker's RPC
	/// port refuses connections (<see cref="engineRestarting"/>) the longer
	/// schedule (1s/4s/16s, ~21s) gives the engine time to come back before the
	/// budget expires. <paramref name="retryCount"/> is the number of retries
	/// already attempted (1-based after the first failure).
	/// </summary>
	internal static TimeSpan PrefillRetryBackoff(int retryCount, bool engineRestarting)
	{
		var normal = new[]
		{
			TimeSpan.FromMilliseconds(500),
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(8),
		};
		var restarting = new[]
		{
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(4),
			TimeSpan.FromSeconds(16),
		};
		var table = engineRestarting ? restarting : normal;
		return table[Math.Clamp(retryCount - 1, 0, table.Length - 1)];
	}

	/// <summary>
	/// #635 fix 2: true when the exception chain contains a connection-refused
	/// socket error — the worker's RPC port is not accepting connections, i.e.
	/// the engine process is down or restarting (not merely busy).
	/// </summary>
	private static bool IsEngineConnectionRefused(Exception ex)
	{
		for (var cur = ex; cur != null; cur = cur.InnerException)
			if (cur is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
				return true;
		return false;
	}

	/// <summary>
	/// Calculate workload-aware BUSY timeouts with optional model-reload headroom.
	/// When <paramref name="modelLoadTimeS"/> is positive, the documented load
	/// time is added (with a safety multiplier) to account for T3 rebuilds that
	/// block the slot during model swaps. #507.
	/// </summary>
	/// <param name="estimatedTokens">Prompt token count from the request. Falls back to 10K if 0.</param>
	/// <param name="modelLoadTimeS">ModelConfig.LoadTimeS for the requested model. 0 = no reload headroom.</param>
	/// <returns>(stuckTimeout, slowTimeout) in milliseconds.</returns>
	internal static (long stuckMs, long slowMs) CalculateBusyTimeouts(long estimatedTokens, int modelLoadTimeS)
	{
		// Conservative prefill rate: 50 tok/s (accounts for slower GPUs like P100 at 28 tok/s decode,
		// but prefill is typically faster). Safety multiplier: 3x to account for variability.
		if (estimatedTokens <= 0) estimatedTokens = 10_000;
		var expectedPrefillMs = (long)(estimatedTokens / 50.0 * 3.0 * 1000.0); // convert seconds to ms
		var stuckMs = Math.Max(60_000, expectedPrefillMs / 2); // at least 60s
		var slowMs = expectedPrefillMs;

		// #507: model-reload headroom. A T3 rebuild (model swap) adds a large
		// fixed cost independent of prompt size. Use a 6x safety multiplier
		// because observed COMBINED reload (270s) was 6x the documented 45s.
		// TODO(#514): that 270s figure is itself the degraded-throughput
		// symptom (engine drops to 2-4 tok/s post-swap, not just a slow
		// reload) — once #514 is fixed, re-measure actual reload time and
		// revisit whether 6x is still the right multiplier (it's likely
		// too generous once reload speed returns to normal).
		if (modelLoadTimeS > 0)
		{
			const int ReloadSafetyMultiplier = 6;
			var reloadHeadroomMs = (long)modelLoadTimeS * ReloadSafetyMultiplier * 1000L;
			stuckMs += reloadHeadroomMs;
			slowMs += reloadHeadroomMs;
		}

		return (stuckMs, slowMs);
	}

	private void EmitTimeline(WorkItem item, string status = "done")
	{
		var node = item.PrefillWorker?.Name ?? item.DecodeWorker?.Name ?? "unknown";
		CoordinatorMetrics.RequestLatency.WithLabels(node, RouteLabel(item))
			.Observe(item.Phases.GetValueOrDefault("total_ms") / 1000.0);
		var prefillNode = item.PrefillWorker != null ? NodeDisplayName(item.PrefillWorker.Name) : "-";
		var decodeNode = item.DecodeWorker != null ? NodeDisplayName(item.DecodeWorker.Name) : "-";
		var prefillModel = item.PrefillWorker != null
			? (_health.GetNodeInfo(item.PrefillWorker.Name)?.CurrentModel ?? "")
			: "";
		var decodeModel = item.DecodeWorker != null
			? (_health.GetNodeInfo(item.DecodeWorker.Name)?.CurrentModel ?? "")
			: "";
		// M-Perf.10: split save into rpc (engine→core) + store (core→Store)
		// for the dashboard; keep save_kv_ms as the sum for back-compat.
		var saveKvRpcMs = item.Phases.GetValueOrDefault("save_kv_rpc_ms");
		var saveKvStoreMs = item.Phases.GetValueOrDefault("save_kv_store_ms");
		var saveKvMs = saveKvRpcMs + saveKvStoreMs;
		var modelLoadMs = item.Phases.GetValueOrDefault("model_load_ms");
		var requestModel = item.Request.GetValueOrDefault("model")?.ToString() ?? "";
		Console.Error.WriteLine(
			$"event=request_timeline timestamp_ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
			$"trace_id={item.TraceId} session_id={item.SessionId} " +
			$"queue_wait_ms={item.Phases.GetValueOrDefault("queue_wait_ms")} node={node} " +
			$"route_type={RouteLabel(item)} " +
			$"prefill_node={KvpValue(prefillNode)} decode_node={KvpValue(decodeNode)} " +
			$"prefill_model={prefillModel} decode_model={decodeModel} request_model={requestModel} " +
			$"prefill_ms={item.Phases.GetValueOrDefault("prefill_ms")} " +
			$"model_load_ms={modelLoadMs} " +
			$"save_kv_ms={saveKvMs} " +
			$"save_kv_rpc_ms={saveKvRpcMs} " +
			$"save_kv_store_ms={saveKvStoreMs} " +
			$"restore_kv_ms={item.Phases.GetValueOrDefault("restore_kv_ms")} " +
			$"decode_ms={item.Phases.GetValueOrDefault("decode_ms")} " +
			$"total_ms={item.Phases.GetValueOrDefault("total_ms")} " +
			$"tokens_in={item.TokensIn} tokens_out={item.TokensOut} kv_bytes={item.KvBytes} " +
			$"prefix_hit={(item.PrefixCacheHit ? "true" : "false")} " +
			$"status={status}"
		);
		Log.Information("event=request_timeline timestamp_ms={TimestampMs} trace_id={TraceId} session_id={SessionId} queue_wait_ms={QueueWaitMs} node={Node} route_type={RouteType} prefill_node={PrefillNode} decode_node={DecodeNode} prefill_model={PrefillModel} decode_model={DecodeModel} request_model={RequestModel} prefill_ms={PrefillMs} model_load_ms={ModelLoadMs} save_kv_ms={SaveKvMs} save_kv_rpc_ms={SaveKvRpcMs} save_kv_store_ms={SaveKvStoreMs} restore_kv_ms={RestoreKvMs} decode_ms={DecodeMs} total_ms={TotalMs} tokens_in={TokensIn} tokens_out={TokensOut} kv_bytes={KvBytes} prefix_hit={PrefixHit} status={Status}",
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			item.TraceId, item.SessionId,
			item.Phases.GetValueOrDefault("queue_wait_ms"), node,
			RouteLabel(item),
			KvpValue(prefillNode), KvpValue(decodeNode),
			prefillModel, decodeModel, requestModel,
			item.Phases.GetValueOrDefault("prefill_ms"), modelLoadMs, saveKvMs,
			saveKvRpcMs, saveKvStoreMs,
			item.Phases.GetValueOrDefault("restore_kv_ms"),
			item.Phases.GetValueOrDefault("decode_ms"),
			item.Phases.GetValueOrDefault("total_ms"),
			item.TokensIn, item.TokensOut, item.KvBytes,
			item.PrefixCacheHit ? "true" : "false",
			status);
	}

	// ── Core KV save helpers (shared by SaveKvAsync + eviction sites) ──

	/// <summary>
	/// #635 fix 4: persist a KV blob to the Store from a background save (the
	/// pipeline's BgSaveAsync and the streaming path's WriteStateToStoreAsync).
	///
	/// Root cause: the pre-decode save (SaveKvAsync) writes the chunked
	/// manifest, but the post-decode bg_save wrote only a plain blob
	/// (OpCode.Put) — the chunk manifest kept referencing the STALE pre-decode
	/// chunks, so a migration continuation's GetManifest restored the old blob
	/// (observed: 501-token pre-decode blob → ~1300 tokens re-prefilled →
	/// prompt_ms=5094 &gt; 5000 budget).
	///
	/// Fix: in chunk mode, ALSO write the chunks + an updated manifest whose
	/// n_past/identity reflect the POST-decode state (ledger NPast is the
	/// post-decode total, updated by TrackAfterCompletion/TrackAfterStream).
	/// The plain Put is retained in BOTH modes: non-chunk storage IS the full
	/// blob, and in chunk mode it backs MigrateSessionAsync's Store Get (which
	/// has no chunk-aware read path).
	/// </summary>
	private async Task PersistKvToStoreAsync(string sessionId, byte[] blob, WorkItem? item, string traceId, CancellationToken ct)
	{
		var storeKey = $"{sessionId}.kv";
		if (_cfg.EnableChunks)
		{
		// Post-decode n_past: the ledger is updated by the decode paths
		// (TrackAfterCompletion/Stream) BEFORE the bg_save runs, so prefer
		// it over item.NPastAfter (which can still hold the PRE-decode
		// prefill count on merged-decode routes).
		var ledgerNPast = _ledger.Lookup(sessionId)?.NPast ?? 0;
		var nPast = ledgerNPast > 0 ? ledgerNPast : (item?.NPastAfter ?? 0);
			var chunks = ChunkEngine.ChunkAndHash(blob);
			var orderedHashes = chunks.Select(c => c.Hash).ToList();
			var missing = await SyncMissingAsync(storeKey, orderedHashes, traceId, ct);
			await PushMissingChunksAsync(storeKey, sessionId, missing, chunks, blob, traceId, ct);
			await PutManifestAsync(storeKey, nPast, blob.Length, chunks, traceId, ct,
				item?.KvModelAlias ?? "", item?.KvTokenizer ?? "", item?.KvModelName ?? "",
				item?.KvModelQuant ?? "", item?.KvModelCapabilities ?? 0, item?.KvModelPath ?? "");
		}
		// Plain Put in both modes — see XML doc above.
		await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put, storeKey, blob, traceId, ct);
	}

	private async Task<byte[]?> SaveKvStateCoreAsync(
		WorkerConfig worker, int slotId, string sessionId, int nPast, string traceId, CancellationToken ct)
	{
		var llamaRpc = GetStateRpcClient(worker);
		var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
			slotId.ToString(), ReadOnlyMemory<byte>.Empty, traceId, ct);
		if (stateResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			return null;

		// M-Perf.9 #289 / #470: capture model identity of the slot that built this KV
		// so the cross-model guard in RestoreKvAsync can detect a model swap
		// between prefill and decode. SlotMeta is enriched with model_alias +
		// tokenizer + model_name + model_quant + model_capabilities + model_path
		// (see META RPC 0x32). On an older binary that doesn't carry the fields,
		// all text fields are "" and capabilities is 0 — the guard skips.
		string modelAlias = "", tokenizer = "", modelName = "", modelQuant = "", modelPath = "";
		uint modelCapabilities = 0;
		var metaResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateMeta,
			slotId.ToString(), ReadOnlyMemory<byte>.Empty, traceId, ct);
		if (metaResp.Status == (byte)Hydra.Shared.StatusCode.Ok
			&& !string.IsNullOrEmpty(metaResp.Meta))
		{
			try
			{
				var meta = JsonSerializer.Deserialize<SlotMeta>(metaResp.Meta);
				if (meta != null)
				{
					modelAlias       = meta.ModelAlias       ?? "";
					tokenizer        = meta.Tokenizer         ?? "";
					modelName        = meta.ModelName         ?? "";
					modelQuant       = meta.ModelQuant        ?? "";
					modelCapabilities = meta.ModelCapabilities;
					modelPath        = meta.ModelPath         ?? "";
				}
			}
			catch (JsonException) { /* keep empty */ }
		}

		var storeKey = $"{sessionId}.kv";
		if (_cfg.EnableChunks)
		{
			var chunks = ChunkEngine.ChunkAndHash(stateResp.Payload);
			var orderedHashes = chunks.Select(c => c.Hash).ToList();
			var missing = await SyncMissingAsync(storeKey, orderedHashes, traceId, ct);
			await PushMissingChunksAsync(storeKey, sessionId, missing, chunks, stateResp.Payload, traceId, ct);
			await PutManifestAsync(storeKey, nPast, stateResp.Payload.Length, chunks, traceId, ct,
				modelAlias, tokenizer, modelName, modelQuant, modelCapabilities, modelPath);
			if (_chunkCache != null)
			{
				// #470 Tier-4: track this session in the L1 registry (see
				// SaveChunkToL1BestEffortAsync) so a later ENOSPC can
				// force-evict it as the oldest non-in-flight candidate.
				RegisterL1Session(sessionId);
				await _chunkCache.SaveHashesAsync(sessionId, orderedHashes, ct);
				foreach (var c in chunks)
					await _chunkCache.SaveChunkDataAsync(sessionId, c.Hash,
						stateResp.Payload.AsSpan(c.Index * _cfg.ChunkSize, Math.Min(_cfg.ChunkSize, (int)(stateResp.Payload.Length - c.Index * _cfg.ChunkSize))).ToArray(), ct);
			}
		}
		else
		{
			await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
				storeKey, stateResp.Payload, traceId, ct);
		}

		return stateResp.Payload;
	}

	/// <summary>Saves a warm lease's KV state to Store before eviction.
	/// Gracefully logs on failure — never blocks the eviction.</summary>
	private async Task SaveSlotStateBeforeEvictAsync(
		string sessionId, string workerName, int slotId, string traceId, CancellationToken ct)
	{
		var w = _cfg.Workers.FirstOrDefault(x => x.Name == workerName);
		if (w == null)
		{
			_log.Warning("evict_save_unknown_worker Sid={Sid} Worker={W}", sessionId, workerName);
			return;
		}
		try
		{
			var nPast = _ledger.Lookup(sessionId)?.NPast ?? 0;
			var payload = await SaveKvStateCoreAsync(w, slotId, sessionId, nPast, traceId, ct);
			if (payload != null)
			{
				_ledger.MarkStoreState(sessionId);
				_log.Information("evict_saved Sid={Sid} Slot={Slot} SizeMB={Size}",
					sessionId, slotId, payload.Length / 1024 / 1024);
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "evict_save_failed Sid={Sid} Slot={Slot} Worker={W}",
				sessionId, slotId, workerName);
		}
	}

	// ── Chunked store helpers ──

	private async Task<List<string>> SyncMissingAsync(string storeKey, List<string> hashes, string traceId, CancellationToken ct)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(hashes);
		var resp = await StoreClient.RequestAsync(OpCode.SyncMissing, storeKey, payload, traceId, ct);
		if (resp.Status != (byte)StatusCode.Ok)
			throw new InvalidDataException($"SYNC_MISSING failed (status=0x{resp.Status:X2})");
		var missing = new List<string>();
		if (resp.Payload is { Length: > 0 })
		{
			using var doc = JsonDocument.Parse(resp.Payload);
			if (doc.RootElement.TryGetProperty("missing_hashes", out var arr))
				foreach (var h in arr.EnumerateArray())
				{
					var s = h.GetString();
					if (!string.IsNullOrEmpty(s)) missing.Add(s);
				}
		}
		return missing;
	}

	internal async Task<int> PushMissingChunksAsync(string storeKey, string sessionId, List<string> missing, List<ChunkRef> allChunks, byte[] stateData, string traceId, CancellationToken ct)
	{
		if (missing.Count == 0) return 0;
		const int BatchBytes = 32 * 1024 * 1024;
		using var batch = new MemoryStream();
		int pending = 0;    // chunks buffered in the current (unflushed) batch
		int pushedOk = 0;   // chunks successfully flushed in prior batches
		async Task FlushAsync()
		{
			if (batch.Length == 0) return;
			// M-Perf / Issue #336: check the response. The Store's PUSH_CHUNKS
			// returns a non-Ok status on transport failure, partial write, or
			// store rejection (e.g. tmpfs full, disk error). Surfacing the
			// status here — instead of letting the cascade fall through to
			// PUT_MANIFEST's "manifest references N unresident chunks" — gives
			// the operator the actual root cause. The throw happens BEFORE
			// PutManifestAsync is called, so the manifest never sees a
			// half-pushed state.
			var resp = await PushChunkBatchAsync(storeKey, sessionId, batch.ToArray(), traceId, ct);
			if (resp.Status != (byte)StatusCode.Ok)
			{
				var reason = StatusReason(resp.Status);
				var total = missing.Count;
				CoordinatorMetrics.PushChunksFailures.WithLabels(reason).Inc();
				_log.Error("push_chunks_failed Sid={Sid} wrote={Wrote}/{Total} status=0x{Status:X2} meta={Meta}",
					sessionId, pushedOk, total, resp.Status, resp.Meta ?? "");
				throw new InvalidDataException(
					$"PUSH_CHUNKS failed (status=0x{resp.Status:X2}): {resp.Meta}");
			}
			pushedOk += pending;
			pending = 0;
			batch.SetLength(0);
		}
		var header = new byte[4];
		foreach (var hash in missing)
		{
			var chunkRef = allChunks.FirstOrDefault(c => c.Hash == hash);
			if (chunkRef == null) continue;
			var offset = chunkRef.Index * _cfg.ChunkSize;
			var size = Math.Min(_cfg.ChunkSize, stateData.Length - offset);
			if (size <= 0) continue;
			var chunkData = stateData.AsSpan(offset, size).ToArray();
			BinaryPrimitives.WriteInt32LittleEndian(header, chunkData.Length);
			batch.Write(header);
			batch.Write(chunkData);
			pending++;
			if (batch.Length >= BatchBytes) await FlushAsync();
		}
		await FlushAsync();
		return pushedOk;
	}

	/// <summary>
	/// Parallel version: splits missing chunks into N groups and pushes them
	/// concurrently via up to <see cref="MaxParallelStoreWrites"/> Store RPCs.
	/// Cuts the ~20s sequential PushChunks for a 2 GB blob down to ~3-5s.
	/// </summary>
	private const int MaxParallelStoreWrites = 4;

	internal async Task<int> PushMissingChunksParallelAsync(string storeKey, string sessionId,
		List<string> missing, List<ChunkRef> allChunks, byte[] stateData,
		string traceId, CancellationToken ct)
	{
		if (missing.Count == 0) return 0;

		// Pre-slice all chunk data so parallel workers read without contention
		var chunkSlices = new List<(int Index, int Size, byte[] Data)>();
		foreach (var hash in missing)
		{
			var chunkRef = allChunks.FirstOrDefault(c => c.Hash == hash);
			if (chunkRef == null) continue;
			var offset = chunkRef.Index * _cfg.ChunkSize;
			var size = Math.Min(_cfg.ChunkSize, stateData.Length - offset);
			if (size <= 0) continue;
			chunkSlices.Add((chunkRef.Index, size, stateData[offset..(offset + size)]));
		}
		if (chunkSlices.Count == 0) return 0;

		// Round-robin partition into N groups
		var groups = new List<List<(int Index, int Size, byte[] Data)>>();
		for (var i = 0; i < MaxParallelStoreWrites; i++) groups.Add([]);
		for (var i = 0; i < chunkSlices.Count; i++)
			groups[i % MaxParallelStoreWrites].Add(chunkSlices[i]);

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var pushedTotal = 0;
		var exceptions = new List<Exception>();

		await Task.WhenAll(groups.Where(g => g.Count > 0).Select(async group =>
		{
			const int BatchBytes = 32 * 1024 * 1024;
			using var batch = new MemoryStream();
			int pending = 0;
			try
			{
				foreach (var (idx, sz, data) in group)
				{
					var hdr = new byte[4];
					BinaryPrimitives.WriteInt32LittleEndian(hdr, sz);
					batch.Write(hdr);
					batch.Write(data);
					pending++;
					if (batch.Length >= BatchBytes)
					{
						var resp = await PushChunkBatchAsync(storeKey, sessionId, batch.ToArray(), traceId, ct);
						if (resp.Status != (byte)StatusCode.Ok)
							throw new InvalidDataException($"PUSH_CHUNKS failed: 0x{resp.Status:X2}");
						Interlocked.Add(ref pushedTotal, pending);
						pending = 0;
						batch.SetLength(0);
					}
				}
				if (batch.Length > 0)
				{
					var resp = await PushChunkBatchAsync(storeKey, sessionId, batch.ToArray(), traceId, ct);
					if (resp.Status != (byte)StatusCode.Ok)
						throw new InvalidDataException($"PUSH_CHUNKS failed: 0x{resp.Status:X2}");
					Interlocked.Add(ref pushedTotal, pending);
				}
			}
			catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
		}));

		sw.Stop();
		_log.Information("push_chunks_parallel Sid={Sid} chunks={Chunks} groups={Groups} ms={Ms} pushed={Pushed}",
			sessionId, chunkSlices.Count, groups.Count(g => g.Count > 0), sw.ElapsedMilliseconds, pushedTotal);

		if (exceptions.Count > 0)
			throw new AggregateException(exceptions);

		return pushedTotal;
	}

	/// <summary>
	/// One PUSH_CHUNKS batch, with evict-on-ENOSPC recovery (#615). The L1
	/// tmpfs chunk cache and the Store's chunk dir share the /mnt/llm-ram
	/// mount, so when the Store rejects a push with "No space left on device"
	/// we evict the L1 byte-LRU immediately (frees the tmpfs) and retry the
	/// batch ONCE. The caller still checks the returned status and throws on
	/// final failure, so the failure path (counter + exception) is unchanged.
	/// <paramref name="sessionId"/> identifies the writing session so forced
	/// ENOSPC eviction (#470 Tier-4) never clears a session that is mid-request.
	/// </summary>
	private async Task<RpcResponse> PushChunkBatchAsync(string storeKey, string sessionId, byte[] batch, string traceId, CancellationToken ct)
	{
		var resp = await StoreClient.RequestAsync(OpCode.PushChunks, storeKey, batch, traceId, ct);
		if (resp.Status == (byte)StatusCode.Ok || !IsEnospcFailure(resp.Meta))
			return resp;

		var evicted = 0;
		if (_chunkCache != null)
		{
			// The L1 byte-budget LRU is a no-op when the L1 is under its own
			// cap — but the L1 shares /mnt/llm-ram with the Store, and a full
			// mount is exactly when bytes must be freed for the retry to
			// succeed. When the LRU freed nothing, force-evict the OLDEST
			// non-in-flight sessions tracked by this scheduler (#470 Tier-4).
			evicted = await _chunkCache.EvictLRUAsync();
			if (evicted == 0)
				evicted += await EvictL1OnEnospcAsync(sessionId);
		}

		var retry = await StoreClient.RequestAsync(OpCode.PushChunks, storeKey, batch, traceId, ct);
		_log.Warning("chunk_cache_evict_on_enospc evicted={Evicted} retry={Retry}",
			evicted, retry.Status == (byte)StatusCode.Ok ? "ok" : "fail");
		return retry;
	}

	/// <summary>
	/// #470 Tier-4: record that this scheduler wrote the session's chunks to
	/// the L1 cache, keyed by write time (Stopwatch ticks). The ENOSPC forced
	/// eviction picks candidates oldest-first from this registry. Idempotent;
	/// re-saving a session bumps it to the front of the eviction order (most
	/// recently written = least likely to be evicted first). Size-capped so a
	/// long-lived coordinator never leaks entries.
	/// </summary>
	private void RegisterL1Session(string sessionId)
	{
		if (string.IsNullOrEmpty(sessionId)) return;
		_l1SessionSavedAt[sessionId] = System.Diagnostics.Stopwatch.GetTimestamp();
		if (_l1SessionSavedAt.Count > MaxL1TrackedSessions)
		{
			foreach (var kv in _l1SessionSavedAt.OrderBy(kv => kv.Value).Take(_l1SessionSavedAt.Count / 2))
				_l1SessionSavedAt.TryRemove(kv.Key, out _);
		}
	}

	/// <summary>
	/// #470 Tier-4: forced L1 eviction on a shared-tmpfs ENOSPC. The L1's own
	/// byte-budget LRU (<see cref="LocalChunkCache.EvictLRUAsync"/>) evicts
	/// nothing when the L1 is under its cap — but the L1 shares /mnt/llm-ram
	/// with the Store's chunk dir, and a full mount is exactly when bytes must
	/// be freed for the save/push retry to succeed. Drop the OLDEST sessions
	/// tracked by this scheduler that are NOT part of an actively-in-flight
	/// request (a session that has already fallen back / parked must not hold
	/// its chunks hostage). Best-effort: each ClearAsync is individually
	/// guarded and the pass is bounded, because the L1 is pure cache (~1 GB per
	/// session) and this runs inside the engine prefill stream / store push.
	/// </summary>
	private async Task<int> EvictL1OnEnospcAsync(string currentSessionId)
	{
		if (_chunkCache == null) return 0;

		// Sessions currently executing a pipeline phase must not be cleared —
		// the caller of the failing write is in-flight by definition.
		var inFlight = _activePipelineSessions.Keys.ToHashSet();
		if (!string.IsNullOrEmpty(currentSessionId)) inFlight.Add(currentSessionId);

		var evicted = 0;
		var candidates = _l1SessionSavedAt
			.OrderBy(kv => kv.Value)      // oldest-first
			.Select(kv => kv.Key)
			.Where(s => !inFlight.Contains(s))
			.Take(MaxL1EnospcEvictions)
			.ToList();

		foreach (var sid in candidates)
		{
			try
			{
				await _chunkCache.ClearAsync(sid);
				_l1SessionSavedAt.TryRemove(sid, out _);
				evicted++;
				_log.Information("chunk_cache_evict_enospc_forced Sid={Sid}", sid);
			}
			catch (Exception ex)
			{
				_log.Warning(ex, "chunk_cache_evict_enospc_clear_failed Sid={Sid}", sid);
			}
		}
		return evicted;
	}

	/// <summary>True when a PUSH_CHUNKS rejection is a full-disk (ENOSPC) error
	/// rather than any other store failure — only ENOSPC merits an eviction
	/// + retry, because evicting the L1 frees space on the shared tmpfs.</summary>
	private static bool IsEnospcFailure(string? meta)
		=> meta is not null
			&& (meta.Contains("No space left", StringComparison.OrdinalIgnoreCase)
				|| meta.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase));

	private static string StatusReason(byte status) => status switch
	{
		(byte)StatusCode.NotFound       => "not_found",
		(byte)StatusCode.Error          => "error",
		(byte)StatusCode.Partial        => "partial",
		(byte)StatusCode.Busy           => "busy",
		(byte)StatusCode.BadRequest     => "bad_request",
		(byte)StatusCode.NotImplemented => "not_implemented",
		_ => $"unknown_0x{status:X2}",
	};

	private async Task PutManifestAsync(
		string storeKey, int nPast, long totalSize, List<ChunkRef> chunks,
		string traceId, CancellationToken ct,
		// M-Perf.9 #289 / #470: model identity of the slot that built this KV. The
		// RestoreKvAsync cross-model guard reads this back via GetManifestAsync
		// so it survives a Coordinator restart. Pre-#470 callers pass ""/0 for
		// all fields; the guard treats "both empty" as "skip".
		string modelAlias = "", string tokenizer = "", string modelName = "",
		string modelQuant = "", uint modelCapabilities = 0, string modelPath = "")
	{
		var manifest = new
		{
			n_past = nPast,
			total_size = totalSize,
			model_alias = modelAlias,
			tokenizer = tokenizer,
			model_name = modelName,
			model_quant = modelQuant,
			model_capabilities = modelCapabilities,
			model_path = modelPath,
			chunks = chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size }),
		};
		var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
		var resp = await StoreClient.RequestAsync(OpCode.PutManifest, storeKey, payload, traceId, ct);
		if (resp.Status != (byte)StatusCode.Ok)
			throw new InvalidDataException($"PUT_MANIFEST failed (status=0x{resp.Status:X2}): {resp.Meta}");
	}

	/// <summary>
	/// #470 Phase 2: ordered KV byte stream from the Store's GET_CHUNKED response.
	/// An empty known-hash list makes the Store return ALL chunks in manifest
	/// order, streamed per-chunk (sendfile) — this reads the response CHUNKED, so
	/// no full blob is ever materialized in coordinator RAM. Framed entries
	/// ([4B index][4B size][data]) are parsed and yielded in order; the consumer
	/// (framed DECODE) writes them straight to the engine socket. Producer errors
	/// (store failure / cancellation) propagate through the enumerable.
	/// </summary>
	private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamKvChunksFromStoreAsync(
		string storeKey, List<ChunkRef> chunks, string traceId,
		[EnumeratorCancellation] CancellationToken ct)
	{
		var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(4)
		{
			FullMode = BoundedChannelFullMode.Wait
		});

		// One frame (8 + chunk data) plus one read window — grows if ever exceeded.
		var frameBuf = new byte[ChunkEngine.CHUNK_SIZE + 8 + 1024 * 1024];
		var frameLen = 0;

		var producer = Task.Run(async () =>
		{
			try
			{
				var knownJson = Encoding.UTF8.GetBytes("[]");
				var resp = await StoreClient.RequestChunkedPayloadAsync(
					OpCode.GetChunked, storeKey, knownJson, traceId, ct,
					onPayloadLen: _ => { },
					onChunk: async (mem, token) =>
					{
						if (frameLen + mem.Length > frameBuf.Length)
							Array.Resize(ref frameBuf, frameLen + mem.Length);
						mem.Span.CopyTo(frameBuf.AsSpan(frameLen));
						frameLen += mem.Length;

						var head = 0;
						while (frameLen - head >= 8)
						{
							// [4B index][4B size][data] — index is informational;
							// ordering is guaranteed by the Store (manifest order).
							var size = BinaryPrimitives.ReadInt32LittleEndian(
								frameBuf.AsSpan(head + 4, 4));
							if (size < 0 || frameLen - head < 8 + size)
								break;
							var data = frameBuf.AsMemory(head + 8, size).ToArray();
							head += 8 + size;
							await channel.Writer.WriteAsync(data, token);
						}
						if (head > 0)
						{
							Buffer.BlockCopy(frameBuf, head, frameBuf, 0, frameLen - head);
							frameLen -= head;
						}
					});
				if (resp.Status != (byte)StatusCode.Ok)
					throw new InvalidDataException(
						$"GET_CHUNKED failed (status=0x{resp.Status:X2}): {resp.Meta}");
				channel.Writer.Complete();
			}
			catch (Exception ex)
			{
				channel.Writer.Complete(ex);
			}
		});

		try
		{
			await foreach (var data in channel.Reader.ReadAllAsync(ct))
				yield return data;
		}
		finally
		{
			await producer; // surface producer failures / cancellation
		}
	}

	/// <summary>Create a blob from chunk-index-ordered data by reading the
	/// missing chunks from the Store and filling known chunks from the
	/// supplied stateData (the previous save's full blob).</summary>
	private async Task<byte[]> AssembleFromChunksAsync(byte[]? prevState, string storeKey, List<ChunkRef> chunks, string traceId, CancellationToken ct)
	{
		var totalSize = chunks.Sum(c => (long)c.Size);
		var blob = new byte[totalSize];

		// Collect hashes the coordinator already has (from previous state data)
		var knownHashes = new HashSet<string>();
		if (prevState != null && prevState.Length > 0)
		{
			// Parallel hash-check + copy for previous state data
			var prevCopyTasks = chunks.Select(c =>
			{
				return Task.Run(() =>
				{
					var offset = c.Index * _cfg.ChunkSize;
					if (offset + c.Size <= prevState.Length)
					{
						var prevHash = ChunkEngine.ComputeHash(prevState.AsSpan(offset, c.Size));
						if (prevHash == c.Hash)
						{
							Array.Copy(prevState, offset, blob, offset, c.Size);
							return (c.Hash, true);
						}
					}
					return (c.Hash, false);
				});
			}).ToList();
			await Task.WhenAll(prevCopyTasks);
			foreach (var (hash, ok) in prevCopyTasks.Select(t => t.Result))
				if (ok) knownHashes.Add(hash);
		}
		// Also check local chunk cache — parallel lookups
		if (_chunkCache != null)
		{
			var remainingChunks = chunks.Where(c => !knownHashes.Contains(c.Hash)).ToList();
			if (remainingChunks.Count > 0)
			{
				var cacheTasks = remainingChunks.Select(c =>
				{
					return Task.Run(async () =>
					{
						var data = await _chunkCache.GetChunkDataAsync(storeKey, c.Hash, ct);
						return (c.Hash, c.Index, data);
					});
				}).ToList();
				await Task.WhenAll(cacheTasks);
				foreach (var (hash, idx, data) in cacheTasks.Select(t => t.Result))
				{
					if (data != null)
					{
						knownHashes.Add(hash);
						var offset = idx * _cfg.ChunkSize;
						Array.Copy(data, 0, blob, offset, data.Length);
					}
				}
			}
		}
		// Fetch remaining missing chunks from Store — parallel groups
		var missingChunks = chunks.Where(c => !knownHashes.Contains(c.Hash)).ToList();
		if (missingChunks.Count > 0)
		{
			// Split missing chunks into parallel groups (up to 4) for concurrent GET_CHUNKED RPCs.
			// Each group sends its OWN known-hash set (everything NOT in this group) so the Store
			// returns exactly that group's chunks.
			const int maxGroups = 4;
			var groups = new List<List<ChunkRef>>();
			for (var i = 0; i < maxGroups; i++) groups.Add([]);
			for (var i = 0; i < missingChunks.Count; i++)
				groups[i % maxGroups].Add(missingChunks[i]);

			var fetchTasks = groups.Where(g => g.Count > 0).Select(async group =>
			{
				// Known = everything EXCEPT this group's chunks
				var groupHashes = new HashSet<string>(group.Select(c => c.Hash));
				var knownForGroup = chunks.Where(c => !groupHashes.Contains(c.Hash)).Select(c => c.Hash).ToList();
				var knownList = JsonSerializer.SerializeToUtf8Bytes(knownForGroup);
				var storeResp = await StoreClient.RequestAsync(OpCode.GetChunked, storeKey, knownList, traceId, ct);
				if (storeResp.Status != (byte)StatusCode.Ok)
					throw new InvalidDataException($"GET_CHUNKED failed (status=0x{storeResp.Status:X2}): {storeResp.Meta}");
				if (storeResp.Payload is { Length: > 0 })
				{
					var off = 0;
					while (off + 8 <= storeResp.Payload.Length)
					{
						var idx = BinaryPrimitives.ReadInt32LittleEndian(storeResp.Payload.AsSpan(off));
						var size = BinaryPrimitives.ReadInt32LittleEndian(storeResp.Payload.AsSpan(off + 4));
						off += 8;
						if (off + size > storeResp.Payload.Length) break;
						var dstOff = idx * _cfg.ChunkSize;
						if (dstOff + size <= blob.Length)
							Array.Copy(storeResp.Payload, off, blob, dstOff, size);
						off += size;
					}
				}
			}).ToList();
			await Task.WhenAll(fetchTasks);
		}
		return blob;
	}

	// ── Gap 4 helpers: n_past tracking ──

	private static int ExtractTotalTokens(Dictionary<string, object> result)
		=> ExtractUsageInt(result, "total_tokens");

	/// <summary>Read an integer field (e.g. prompt_tokens, completion_tokens) from the
	/// OpenAI-style usage object, returning 0 when absent.</summary>
	internal static int ExtractUsageInt(Dictionary<string, object> result, string field)
	{
		if (!result.TryGetValue("usage", out var u) || u is not JsonElement ue)
			return 0;
		if (ue.ValueKind != JsonValueKind.Object || !ue.TryGetProperty(field, out var v)
			|| v.ValueKind != JsonValueKind.Number)
			return 0;
		return v.GetInt32();
	}

	private void TrackAfterCompletion(string sessionId, Dictionary<string, object> result)
	{
		var total = ExtractTotalTokens(result);
		if (total > 0)
		{
			_ledger.UpdateNPast(sessionId, total);
			var promptTokens = ExtractUsageInt(result, "prompt_tokens");
			if (promptTokens > 0)
				_ledger.UpdateNPromptTokens(sessionId, promptTokens);
			var entry = _ledger.Lookup(sessionId);
			if (entry != null && !entry.SlotId.HasValue)
				ResolveSlotFromHealth(sessionId, total);
		}
	}

	private void TrackAfterStream(string sessionId, Dictionary<string, object>? lastUsage)
	{
		if (lastUsage == null)
			return;

		var total = ExtractTotalTokens(lastUsage);
		if (total > 0)
		{
			_ledger.UpdateNPast(sessionId, total);
			var promptTokens = ExtractUsageInt(lastUsage, "prompt_tokens");
			if (promptTokens > 0)
				_ledger.UpdateNPromptTokens(sessionId, promptTokens);
			var entry = _ledger.Lookup(sessionId);
			if (entry != null && !entry.SlotId.HasValue)
				ResolveSlotFromHealth(sessionId, total);
		}
	}

	private async IAsyncEnumerable<byte[]> TrackStreamNPast(
		IAsyncEnumerable<byte[]> source, WorkItem item,
		bool mergedPath = false, Dictionary<string, object>? fallbackRequestBody = null,
		string? fallbackNodeUrl = null,
		CancellationToken fallbackCt = default)
	{
		string? lastUtf8 = null;
		// #616 merged path: one-chunk lookahead — hold the LAST data chunk and
		// the trailing `data: [DONE]` (when present) so an empty-content stream
		// can be re-issued via the HTTP proxy before the SSE ends. Everything
		// else is relayed live, exactly as before.
		byte[]? heldChunk = null;
		byte[]? heldDone = null;
		bool sawContent = false;

		await foreach (var chunk in source)
		{
			var isDone = false;
			if (chunk.Length > 0)
			{
				var s = Encoding.UTF8.GetString(chunk).Trim();
				if (s == "data: [DONE]")
				{
					isDone = true;
				}
				else
				{
					lastUtf8 = s;
					if (!sawContent && HasNonEmptyContentDelta(s))
						sawContent = true;
				}
			}

			if (!mergedPath)
			{
				// HTTP path: identical behavior to pre-#616 — relay as-is.
				yield return chunk;
				continue;
			}

			if (isDone)
			{
				heldDone = chunk;
				continue;
			}
			if (heldChunk != null)
				yield return heldChunk;
			heldChunk = chunk;
		}

		if (lastUtf8 != null)
		{
			try
			{
				var trimmed = lastUtf8.Trim();
				if (trimmed.StartsWith("data: ") && trimmed != "data: [DONE]")
				{
					var json = trimmed[6..];
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
					if (data != null)
					{
						// Preferred: usage object (OpenAI-compat, present in non-streaming passthrough)
						if (data.TryGetValue("usage", out var u))
						{
							var usageDict = new Dictionary<string, object>
							{
								["usage"] = u
							};
							item.TokensIn = ExtractUsageInt(usageDict, "prompt_tokens");
							item.TokensOut = ExtractUsageInt(usageDict, "completion_tokens");
							TrackAfterStream(item.SessionId, usageDict);
						}
						// Fallback: timings object (llama-server streaming chunks)
						if (data.TryGetValue("timings", out var t) && t.ValueKind == JsonValueKind.Object)
						{
							if (item.TokensIn == 0 && t.TryGetProperty("prompt_n", out var pn) && pn.ValueKind == JsonValueKind.Number)
								item.TokensIn = pn.GetInt32();
							if (item.TokensOut == 0 && t.TryGetProperty("predicted_n", out var dn) && dn.ValueKind == JsonValueKind.Number)
								item.TokensOut = dn.GetInt32();
							// Engine mode (cold_combined / inline prefill): PrefillAsync was
							// skipped, so item.NPastAfter was never set and the ledger still
							// holds NPast=0. Record the prompt size from timings so the next
							// turn's NewPromptTokens sees a real baseline and warm affinity
							// survives instead of being evicted as a full re-prefill. Mirrors
							// TrackAfterStream but uses the engine's prompt_n directly.
							if (item.TokensIn > 0)
							{
								item.NPastAfter = item.TokensIn;
								_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
								_ledger.UpdateNPromptTokens(item.SessionId, item.TokensIn);
								var e = _ledger.Lookup(item.SessionId);
								if (e != null && !e.SlotId.HasValue)
									ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
							}
						// Engine mode: PrefillAsync was skipped (RouteDecision→Decode),
						// so prefill_ms was never recorded. Backfill from the engine's
						// prompt_ms timing so the Grafana timeline shows the prefill bar.
						// Store on the item so FinalizeStreamPhases can split decode_ms.
						if (!item.Phases.ContainsKey("prefill_ms") || item.Phases["prefill_ms"] == 0)
						{
							if (t.TryGetProperty("prompt_ms", out var pm) && pm.ValueKind == JsonValueKind.Number)
							{
								item.EnginePrefillMs = (long)pm.GetDouble();
								item.Phases["prefill_ms"] = item.EnginePrefillMs;
								_log.Information("prefill_backfill_from_timings Sid={Sid} prompt_ms={Ms}",
									item.SessionId, item.EnginePrefillMs);
							}
						}
						}

						// #470: extract hydra_metrics fields from the streaming chunk.
						// These fields are emitted by the engine when the merged decode
						// path is active and carry decode_request_id, id_slot, n_past,
						// kv_bytes, decode_ms, prompt_ms, model_identity, and match.
						if (data.TryGetValue("hydra_metrics", out var hmEl) && hmEl.ValueKind == JsonValueKind.Object)
						{
							CoordinatorMetrics.StreamingHydraMetricsReceived.Inc();
							if (hmEl.TryGetProperty("decode_request_id", out var dri) && dri.ValueKind == JsonValueKind.Number)
								item.DecodeRequestId = dri.GetInt32();
							if (hmEl.TryGetProperty("id_slot", out var ids) && ids.ValueKind == JsonValueKind.Number)
								item.LastIdSlot = ids.GetInt32();
							if (hmEl.TryGetProperty("n_past", out var np) && np.ValueKind == JsonValueKind.Number)
							{
								item.NPastAfter = np.GetInt32();
								_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
							}
							if (hmEl.TryGetProperty("kv_bytes", out var kv) && kv.ValueKind == JsonValueKind.Number)
								item.KvBytes = kv.GetInt64();
							if (hmEl.TryGetProperty("decode_ms", out var dm) && dm.ValueKind == JsonValueKind.Number)
							{
								item.Phases["decode_ms"] = (long)dm.GetDouble();
								// #620 Task 3/3a: engine-reported decode duration from
								// hydra_metrics.decode_ms (authoritative engine field).
								item.Phases["engine_decode_ms"] = item.Phases["decode_ms"];
								CoordinatorMetrics.EngineDecodeMs
									.WithLabels(item.DecodeWorker?.Name ?? "unknown")
									.Observe(item.Phases["engine_decode_ms"]);
							}
							if (hmEl.TryGetProperty("prompt_ms", out var prm) && prm.ValueKind == JsonValueKind.Number)
								item.Phases["prefill_ms"] = (long)prm.GetDouble();

							// Extract model_identity sub-object
							if (hmEl.TryGetProperty("model_identity", out var mi) && mi.ValueKind == JsonValueKind.Object)
							{
								if (mi.TryGetProperty("tokenizer", out var tok) && tok.ValueKind == JsonValueKind.String)
									item.KvTokenizer = tok.GetString();
								if (mi.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String)
									item.KvModelName = mn.GetString();
								if (mi.TryGetProperty("model_quant", out var mq) && mq.ValueKind == JsonValueKind.String)
									item.KvModelQuant = mq.GetString();
								if (mi.TryGetProperty("model_capabilities", out var mc) && mc.ValueKind == JsonValueKind.Number)
									item.KvModelCapabilities = mc.GetUInt32();
							}

							// Extract match sub-object
							if (hmEl.TryGetProperty("match", out var mtch) && mtch.ValueKind == JsonValueKind.Object)
							{
								var tokM = mtch.TryGetProperty("tokenizer_match", out var tm2) && tm2.GetBoolean();
								var nmM = mtch.TryGetProperty("model_name_match", out var nm2) && nm2.GetBoolean();
								var cmM = mtch.TryGetProperty("model_capabilities_match", out var cm2) && cm2.GetBoolean();
								uint cx = mtch.TryGetProperty("capabilities_xor", out var cx2) ? cx2.GetUInt32() : 0;
								var qmM = mtch.TryGetProperty("model_quant_match", out var qm2) && qm2.GetBoolean();
								var amM = mtch.TryGetProperty("model_alias_match", out var am2) && am2.GetBoolean();
								item.Match = new DecodeMatch(tokM, nmM, cmM, cx, qmM, amM);
							}

							_log.Information("hydra_metrics_extracted Sid={Sid} DecodeId={Did} Slot={Slot} NPast={N} KvBytes={Kb} DecodeMs={Dm} PromptMs={Pm}",
								item.SessionId, item.DecodeRequestId, item.LastIdSlot,
								item.NPastAfter, item.KvBytes,
								item.Phases.TryGetValue("decode_ms", out var dMs) ? dMs : 0,
								item.Phases.TryGetValue("prefill_ms", out var pMs) ? pMs : 0);
						}
					}
				}
			}
			catch { }
			Console.Error.WriteLine($"track_stream_tokens Sid={item.SessionId} Trace={item.TraceId} TokensIn={item.TokensIn} TokensOut={item.TokensOut} LastUtf8={lastUtf8?[..Math.Min(200, lastUtf8?.Length ?? 0)]}");
		}

		// #616/#642/#588 merged path: the stream ended. If the engine generated but
		// neither content NOR reasoning_content NOR tool_calls was seen (the engine
		// 097d13e/b95c228b delivers content, reasoning_content and tool_calls in the
		// merged DONE delta — a reasoning- or tool-call-only reply must not pay a
		// double run), re-issue ONCE via the HTTP proxy (non-stream)
		// and emit the fallback's content as the final SSE chunk, then [DONE].
		// Bounded to a single attempt — no loops.
		if (mergedPath)
		{
			// #622: arm the gate on ENGINE-GENERATION evidence, not
			// usage-based TokensOut. The merged COMPLETION DONE SSE delta
			// carries hydra_metrics (decode_ms > 0 once the engine generated)
			// but NO usage — include_usage never propagates through merged
			// COMPLETION — so TokensOut stayed 0 and the old
			// (TokensOut > 0 && !sawContent) gate never fired, relaying an
			// empty response live (#31460310245). decode_ms > 0 is the
			// generation signal (parsed above from hydra_metrics); !sawContent
			// is the content check. Edge: a genuinely-zero-token valid reply
			// (engine truly produced nothing) may still report decode_ms > 0
			// and trigger ONE fallback re-issue — bounded (single attempt) and
			// the HTTP proxy returns the truth.
			var engineGenerated = item.Phases.TryGetValue("decode_ms", out var decodeMs) && decodeMs > 0;

			// #622 follow-up (live retest 15:07): the relay-branch stream's
			// terminal chunk carries NO hydra_metrics at all (relayed partials
			// + bare [DONE] carry only content+timings), so decode_ms never
			// reached Phases and the gate above can't arm — the empty response
			// relays verbatim. When the stream ended content-less AND never
			// reported decode_ms, issue ONE final GET to the DONE-state result
			// endpoint (the buffered path's PollDecodeResultAsync, same decode
			// id) as a SECOND signal source: its DONE JSON carries
			// hydra_metrics.decode_ms. decode_ms > 0 → the engine generated →
			// run the existing fallback. Bounded: one attempt, own 10s
			// timeout, no loop, no change to the buffered path or wire
			// protocol. The stream-present hydra_metrics gate above is
			// untouched — this fires only when the stream lacked the signal.
			if (!sawContent && !item.Phases.ContainsKey("decode_ms")
				&& item.DecodeRequestId is > 0
				&& !string.IsNullOrEmpty(fallbackNodeUrl ?? item.DecodeWorker?.LlamaUrl))
			{
				try
				{
					using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(fallbackCt);
					doneCts.CancelAfter(TimeSpan.FromSeconds(10));
					var doneResult = await _proxy.PollDecodeResultAsync(
						fallbackNodeUrl ?? item.DecodeWorker?.LlamaUrl ?? "",
						item.DecodeRequestId.Value, item.TraceId, doneCts.Token);
					if (doneResult.TryGetValue("hydra_metrics", out var hmRaw)
						&& hmRaw is JsonElement hmEl)
					{
						if (hmEl.ValueKind == JsonValueKind.Object
							&& hmEl.TryGetProperty("decode_ms", out var dm)
							&& dm.ValueKind == JsonValueKind.Number
							&& dm.GetDouble() > 0)
						{
							item.Phases["decode_ms"] = (long)dm.GetDouble();
							// #620 Task 3/3a: DONE-state result carries the same
							// authoritative hydra_metrics.decode_ms as the stream.
							item.Phases["engine_decode_ms"] = item.Phases["decode_ms"];
							CoordinatorMetrics.EngineDecodeMs
								.WithLabels(item.DecodeWorker?.Name ?? "unknown")
								.Observe(item.Phases["engine_decode_ms"]);
							engineGenerated = true;
							_log.Information("merged_decode_done_state_metrics Sid={Sid} Did={Did} DecodeMs={Ms}",
								item.SessionId, item.DecodeRequestId, (long)dm.GetDouble());
						}
					}
				}
				catch (Exception ex)
				{
					// Fetch failed (timeout / transport / 404 exhaustion) —
					// the held chunks relay as today, no fallback.
					_log.Warning(ex, "merged_decode_done_state_fetch_failed Sid={Sid} Did={Did}",
						item.SessionId, item.DecodeRequestId);
				}
			}

			var needFallback = engineGenerated && !sawContent;
			Dictionary<string, object>? fallback = null;
			if (needFallback)
			{
				try
				{
					// #616 QA: re-issue with the CLEAN client body, forced to
					// non-stream and with stream_options stripped — the real
					// engine answers SSE for stream:true bodies and the proxy
					// deserializes a JSON dict (CompletionProxyService).
					var fallbackBody = new Dictionary<string, object>(fallbackRequestBody)
					{
						["stream"] = false
					};
					fallbackBody.Remove("stream_options");
					fallback = await _proxy.ProxyCompletionAsync(
						fallbackNodeUrl ?? item.DecodeWorker?.LlamaUrl ?? "",
						fallbackBody, item.TraceId, fallbackCt);
					_log.Warning("merged_decode_empty_content_fallback sid={Sid} tokens={N}",
						item.SessionId, item.TokensOut);
					if (fallback.TryGetValue("id_slot", out var fId) && fId is JsonElement fEl)
						item.LastIdSlot = fEl.GetInt32();
					item.TokensIn = ExtractUsageInt(fallback, "prompt_tokens");
					item.TokensOut = ExtractUsageInt(fallback, "completion_tokens");
				}
				catch (Exception ex)
				{
					// The fallback failed — log and emit the merged result as-is
					// (the empty-content stream already went out live).
					_log.Warning(ex, "merged_decode_empty_content_fallback_failed sid={Sid}",
						item.SessionId);
				}
			}
			// Happy path / fallback-completed: relay held chunks in original order.
			// On a successful fallback the held final chunk (empty content +
			// usage) is REPLACED by the fallback chunk — the fallback carries
			// its own content and usage.
			if (fallback != null)
				yield return BuildFallbackSseChunk(fallback);
			else if (heldChunk != null)
				yield return heldChunk;
			if (heldDone != null)
				yield return heldDone;
			else
				// #616 QA: synthetic terminator — the engine's DONE-state
				// stream emits a single delta chunk and closes without
				// `data: [DONE]`; streaming clients must always see one.
				yield return Encoding.UTF8.GetBytes("data: [DONE]\n\n");
		}
	}

	/// <summary>
	/// #616/#642: true when the merged-decode result produced tokens but BOTH
	/// choices[0].message.content AND choices[0].message.reasoning_content are
	/// blank — only then is the empty-content fallback re-issue needed. The
	/// engine (097d13e) delivers reasoning_content in the merged result
	/// (server-context.cpp DONE handler), so a reasoning-only reply (empty
	/// content, non-empty reasoning_content) must NOT be re-issued — that
	/// would run the completion a second time. #588: a reply carrying
	/// choices[0].message.tool_calls (the engine fix b95c228b emits it in the
	/// merged DONE result) is likewise NOT re-issued — re-issuing would discard
	/// the engine's tool_calls and run the completion a second time.
	/// </summary>
	internal static bool MergedDecodeResultHasEmptyContent(
		Dictionary<string, object> result, out int tokensOut)
	{
		tokensOut = ExtractUsageInt(result, "completion_tokens");
		return tokensOut > 0
			&& string.IsNullOrWhiteSpace(ExtractChoiceContent(result))
			&& string.IsNullOrWhiteSpace(ExtractChoiceReasoningContent(result))
			&& !ExtractChoiceHasToolCalls(result);
	}

	/// <summary>Read choices[0].message.content from an OpenAI-style completion
	/// dictionary ("" when absent).</summary>
	private static string ExtractChoiceContent(Dictionary<string, object> result)
	{
		if (!result.TryGetValue("choices", out var c) || c is not JsonElement ce
			|| ce.ValueKind != JsonValueKind.Array || ce.GetArrayLength() == 0)
			return "";
		var choice = ce[0];
		if (choice.TryGetProperty("message", out var msg)
			&& msg.TryGetProperty("content", out var ct)
			&& ct.ValueKind == JsonValueKind.String)
			return ct.GetString() ?? "";
		return "";
	}

	/// <summary>#642: read choices[0].message.reasoning_content from an
	/// OpenAI-style completion dictionary ("" when absent). The engine
	/// (097d13e) populates it in the merged DONE result.</summary>
	private static string ExtractChoiceReasoningContent(Dictionary<string, object> result)
	{
		if (!result.TryGetValue("choices", out var c) || c is not JsonElement ce
			|| ce.ValueKind != JsonValueKind.Array || ce.GetArrayLength() == 0)
			return "";
		var choice = ce[0];
		if (choice.TryGetProperty("message", out var msg)
			&& msg.TryGetProperty("reasoning_content", out var rc)
			&& rc.ValueKind == JsonValueKind.String)
			return rc.GetString() ?? "";
		return "";
	}

	/// <summary>#588: true when choices[0].message.tool_calls is a non-empty
	/// array. The engine fix (b95c228b) emits the tool_calls array in merged
	/// DONE results; a tool-call reply (empty content, no reasoning) must be
	/// returned verbatim, never discarded by the empty-content fallback.</summary>
	private static bool ExtractChoiceHasToolCalls(Dictionary<string, object> result)
	{
		if (!result.TryGetValue("choices", out var c) || c is not JsonElement ce
			|| ce.ValueKind != JsonValueKind.Array || ce.GetArrayLength() == 0)
			return false;
		var choice = ce[0];
		if (!choice.TryGetProperty("message", out var msg))
			return false;
		return msg.TryGetProperty("tool_calls", out var tc)
			&& tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0;
	}

	/// <summary>#616 QA: deep clone the client request body via a JSON
	/// round-trip. The clone is a snapshot of the ORIGINAL request — immune to
	/// later in-place mutation (id_slot / hydra_config / stream_options
	/// injection) — so the empty-content fallback re-issues the same
	/// messages/model/max_tokens the client sent.</summary>
	private static Dictionary<string, object> DeepCloneRequestBody(Dictionary<string, object> request)
		=> JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(request))!;

	/// <summary>#616/#642: true when an SSE data line carries a non-empty
	/// choices[0].delta.content OR choices[0].delta.reasoning_content — either
	/// counts as content delivered, so a reasoning-only stream (content empty,
	/// reasoning_content populated) does NOT trigger the empty-content fallback
	/// re-issue. The engine (097d13e) delivers reasoning_content in the merged
	/// DONE delta (server-context.cpp DONE handler). #588: a non-empty
	/// choices[0].delta.tool_calls array (the engine fix b95c228b emits it in
	/// merged DONE deltas) counts as content delivered too — a tool-call-only
	/// stream must relay verbatim, never re-issued via the HTTP proxy.</summary>
	private static bool HasNonEmptyContentDelta(string sseLine)
	{
		var trimmed = sseLine.Trim();
		if (!trimmed.StartsWith("data: ") || trimmed == "data: [DONE]")
			return false;
		try
		{
			var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(trimmed[6..]);
			if (data == null || !data.TryGetValue("choices", out var choices)
				|| choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
				return false;
			var choice = choices[0];
			if (!choice.TryGetProperty("delta", out var delta))
				return false;
			if (delta.TryGetProperty("content", out var content)
				&& content.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(content.GetString()))
				return true;
			if (delta.TryGetProperty("reasoning_content", out var reasoning)
				&& reasoning.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(reasoning.GetString()))
				return true;
			if (delta.TryGetProperty("tool_calls", out var toolCalls)
				&& toolCalls.ValueKind == JsonValueKind.Array
				&& toolCalls.GetArrayLength() > 0)
				return true;
		}
		catch { }
		return false;
	}

	/// <summary>#616/#642: build ONE SSE chat.completion.chunk from a buffered
	/// HTTP proxy response so streaming clients receive the fallback's content
	/// (and usage) as the stream's final event. reasoning_content is carried
	/// into the delta when present — when the fallback DOES fire (both fields
	/// empty in the merged result), any reasoning content the HTTP proxy
	/// returns must still reach the client. Mirrors server-chat.cpp:453-464:
	/// the message is emitted when EITHER content or reasoning_content is
	/// non-empty. #588: message.tool_calls (the engine fix b95c228b emits it
	/// in merged-decode results) is copied into the delta VERBATIM — the
	/// coordinator never re-shapes the OpenAI tool-call schema.</summary>
	private static byte[] BuildFallbackSseChunk(Dictionary<string, object> fallback)
	{
		string content = "";
		string reasoningContent = "";
		JsonElement? toolCalls = null;
		string? finishReason = "stop";
		if (fallback.TryGetValue("choices", out var ch) && ch is JsonElement chEl
			&& chEl.ValueKind == JsonValueKind.Array && chEl.GetArrayLength() > 0)
		{
			var choice = chEl[0];
			if (choice.TryGetProperty("message", out var msg))
			{
				if (msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
					content = ct.GetString() ?? "";
				if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
					reasoningContent = rc.GetString() ?? "";
				if (msg.TryGetProperty("tool_calls", out var tc)
					&& tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0)
					toolCalls = tc;
			}
			if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
				finishReason = fr.GetString();
		}
		var id = fallback.TryGetValue("id", out var idV) && idV is string idS
			? idS : "chatcmpl-fallback";
		var model = fallback.TryGetValue("model", out var mV) && mV is string mS ? mS : "";
		long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		if (fallback.TryGetValue("created", out var crV) && crV is JsonElement crEl
			&& crEl.ValueKind == JsonValueKind.Number)
			created = (long)crEl.GetDouble();
		var usage = fallback.TryGetValue("usage", out var uV) ? uV : null;

		var delta = new Dictionary<string, object?>
		{
			["role"] = "assistant",
			["content"] = content,
		};
		if (!string.IsNullOrEmpty(reasoningContent))
			delta["reasoning_content"] = reasoningContent;
		if (toolCalls.HasValue)
			delta["tool_calls"] = toolCalls.Value;

		var chunk = new Dictionary<string, object?>
		{
			["id"] = id,
			["object"] = "chat.completion.chunk",
			["created"] = created,
			["model"] = model,
			["choices"] = new object?[]
			{
				new Dictionary<string, object?>
				{
					["index"] = 0,
					["delta"] = delta,
					["finish_reason"] = finishReason,
				}
			},
			["usage"] = usage,
		};
		return Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(chunk)}\n\n");
	}

	private void ResolveSlotFromHealth(string sessionId, int totalTokens)
	{
		var entry = _ledger.Lookup(sessionId);
		if (entry == null)
			return;

		var info = _health.GetHealthSummary();
		if (!info.TryGetValue(entry.NodeName, out var nodeObj)
			|| nodeObj is not Dictionary<string, object> nodeDict)
			return;

		if (!nodeDict.TryGetValue("slots", out var slotsObj)
			|| slotsObj is not JsonElement slotsEl
			|| slotsEl.ValueKind != JsonValueKind.Array)
			return;

		foreach (var s in slotsEl.EnumerateArray())
		{
			var nPast = s.TryGetProperty("n_past", out var sn) ? sn.GetInt32() : 0;
			var isProcessing = s.TryGetProperty("is_processing", out var ip) && ip.GetBoolean();
			var id = s.TryGetProperty("id", out var si) ? si.GetInt32() : 0;

			if (nPast == totalTokens && !isProcessing)
			{
				lock (entry)
				{
					entry.SlotId = id;
				}
				_log.Information("slot_resolved_health Sid={Sid} Slot={Slot} NPast={N}",
					sessionId, id, totalTokens);
				return;
			}
		}
	}

	// ── Gap 7: migrate session (called from controller) ──

	/// <summary>
	/// Cached per-worker LlamaClient (HTTP GET /health, /slots/{id}/state/meta…).
	/// ConcurrentDictionary: the #597 stale-unhealthy liveness probe now calls
	/// <see cref="GetLlamaClient"/> from parallel Task.WhenAll probes, so the
	/// first-time cache population (read + write) must be thread-safe.
	/// </summary>
	private readonly ConcurrentDictionary<string, LlamaClient> _llamaClients = new();

	private LlamaClient GetLlamaClient(WorkerConfig w)
	{
		if (_llamaClients.TryGetValue(w.Name, out var c)) return c;
		if (LlamaClientFactory != null)
		{
			c = LlamaClientFactory(w.Name);
		}
		else
		{
			// Go through IHttpClientFactory (matches HealthMonitorService) instead of
			// a raw `new HttpClient()`. This instance is cached in _llamaClients for
			// the process lifetime, so it isn't a per-call leak — but the BUSY-retry
			// progress query (GetStateMetaAsync, fired on every SignalEvaluator() wake
			// while a slot stays busy, unthrottled by any polling interval) is the one
			// production path hitting GET /slots/{id}/state/meta, the exact endpoint
			// implicated in issue #552's TIME_WAIT spikes. Routing it through the
			// factory gets it named HTTP logging (client "llama-{worker}") so a future
			// recurrence is attributable from logs instead of live /proc forensics.
			var http = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient($"llama-{w.Name}");
			http.Timeout = TimeSpan.FromMinutes(5);
			c = new LlamaClient(http, w.LlamaUrl);
		}
		_llamaClients[w.Name] = c;
		return c;
	}

	private Hydra.Shared.RpcClient GetAgent(WorkerConfig w)
	{
		if (_agentClients.TryGetValue(w.Name, out var c)) return c;
		var client = AgentClientFactory != null
			? AgentClientFactory(w.Host, w.RpcPort)
			: new Hydra.Shared.RpcClient(w.Host, w.RpcPort);
		_agentClients[w.Name] = client;
		return client;
	}

	internal Hydra.Shared.RpcClient GetLlamaRpcClient(WorkerConfig w)
		=> GetOrCreateRpcClient(_llamaRpcClients, w, (client, name) => _ = ConfigureStateChunkSizeAsync(client, name));

	/// <summary>
	/// Per-worker RpcClient dedicated to large state transfers (STATE_GET /
	/// STATE_PUT). #581: the engine's hydra RPC loop is strictly serial per
	/// connection, so a multi-hundred-MB STATE_GET response stream would hold
	/// the shared connection's _sync for its whole duration and block the next
	/// turn's STATE_META/DECODE RPCs. A second connection lets the engine queue
	/// the DECODE behind the stream (its inference thread is busy either way)
	/// instead of the coordinator failing its own request timeout. Same
	/// factory as <see cref="GetLlamaRpcClient"/> so tests route both to the
	/// same fake.
	/// </summary>
	internal Hydra.Shared.RpcClient GetStateRpcClient(WorkerConfig w)
		=> GetOrCreateRpcClient(_llamaStateRpcClients, w);

	/// <summary>
	/// Shared create-or-get for the per-worker RPC client caches (#600).
	/// <see cref="GetLlamaRpcClient"/> and <see cref="GetStateRpcClient"/> only
	/// differ in which cache they populate; construction and caching live here
	/// so future client-creation changes can't drift out of sync. The caches
	/// are plain dictionaries with the same lock-free check-then-set pattern
	/// as <see cref="GetAgent"/> — an existing duplicate creation is simply
	/// overwritten, which matches the pre-refactor behavior. <paramref
	/// name="onCreated"/> fires only on first creation so per-cache one-time
	/// side effects (e.g. the llama cache's state-chunk CONFIGURE) are kept.
	/// </summary>
	private Hydra.Shared.RpcClient GetOrCreateRpcClient(
		Dictionary<string, Hydra.Shared.RpcClient> cache,
		WorkerConfig w,
		Action<Hydra.Shared.RpcClient, string>? onCreated = null)
	{
		if (cache.TryGetValue(w.Name, out var c)) return c;
		var rpcHost = w.LlamaRpcHost;
		// Honor the injectable factory so tests never open real sockets.
		var client = AgentClientFactory != null
			? AgentClientFactory(rpcHost, w.LlamaRpcPort)
			: new Hydra.Shared.RpcClient(rpcHost, w.LlamaRpcPort);
		cache[w.Name] = client;
		onCreated?.Invoke(client, w.Name);
		return client;
	}

	/// <summary>
	/// hydra#334: tell the engine what chunk size to use for STATE_GET socket
	/// streaming (llama_io_write_socket) instead of relying on its compiled-in
	/// default. Fire-and-forget, best-effort: legacy llama-server binaries don't
	/// support CONFIGURE (0x40-0x46) and a failure here just leaves the engine on
	/// its own 2 MiB default — never blocks or fails client creation.
	/// </summary>
	private async Task ConfigureStateChunkSizeAsync(Hydra.Shared.RpcClient client, string workerName)
	{
		try
		{
			// "0" is safe here even though CONFIGURE is framed per-slot on the wire:
			// every slot in a llama-engine process shares one llama_context
			// (server-context.cpp's slot-init loop sets slot.ctx_tgt = ctx_tgt for
			// all slots), so configuring slot 0 applies engine-wide — same scope
			// as the existing SET_EXPERT_MODE call below.
			var configJson = $"{{\"state_chunk_size\":{_cfg.StateChunkSizeBytes}}}";
			var resp = await client.EngineConfigureAsync("0", configJson, "startup-configure", CancellationToken.None);
			if (resp.Status != (byte)StatusCode.Ok)
			{
				_log.Warning("engine_configure_state_chunk_size_rejected Worker={Worker} Status={Status}", workerName, resp.Status);
				return;
			}
			// The engine clamps to [64 KiB, 64 MiB] and echoes the post-clamp value
			// as "state_chunk_size_applied" — surface a mismatch instead of silently
			// trusting the OK status (the engine would otherwise look "configured"
			// while actually running on a different chunk size than requested).
			if (!string.IsNullOrWhiteSpace(resp.Meta))
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
				if (meta?.TryGetValue("state_chunk_size_applied", out var applied) == true
					&& applied.TryGetInt64(out var appliedBytes)
					&& appliedBytes != _cfg.StateChunkSizeBytes)
				{
					_log.Warning("engine_configure_state_chunk_size_clamped Worker={Worker} Requested={Requested} Applied={Applied}",
						workerName, _cfg.StateChunkSizeBytes, appliedBytes);
				}
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "engine_configure_state_chunk_size_failed Worker={Worker}", workerName);
		}
	}
}
