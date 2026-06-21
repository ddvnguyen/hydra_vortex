using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
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
	private readonly Channel<WorkItem> _mainQueue;
	private readonly Channel<WorkItem> _prefillQueue;
	private readonly Channel<WorkItem> _decodeQueue;
	private readonly CancellationTokenSource _cts = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _agentClients = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _llamaRpcClients = new();
	private readonly HashSet<string> _prefixSet = [];

	/// <summary>
	/// Injectable factory for creating RPC clients (agent + llama binary RPC).
	/// Set in tests to return tracking test doubles instead of real sockets.
	/// </summary>
	internal Func<string, int, Hydra.Shared.RpcClient>? AgentClientFactory { get; set; }
	private readonly ConcurrentDictionary<string, SlotLease> _warmLeases = new();
	// Two-engine "work together": peer-engine leases held for the request's duration,
	// and the active mode per session (for the active-sessions gauge on release).
	private readonly ConcurrentDictionary<string, SlotLease> _peerLeases = new();
	private readonly ConcurrentDictionary<string, string> _activeMultiSessions = new();
	private readonly ConcurrentDictionary<string, byte> _streamCompleted = new(); // traceIds whose streaming has finished
	private readonly ConcurrentDictionary<string, (string WorkerName, int SlotId, string TraceId)> _pendingBgSaves = new();
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _pipelineCts = new();
	// Streaming requests whose request_timeline emit is deferred until the stream
	// finishes — decode_ms/total_ms must cover the full stream, not just dispatch.
	internal readonly ConcurrentDictionary<string, WorkItem> _pendingTimelines = new();
	private readonly SemaphoreSlim _decodeSlotSignal = new(0, int.MaxValue);

	private static BoundedChannelOptions ChannelOpts(int capacity) => new(capacity)
	{
		FullMode = BoundedChannelFullMode.Wait,
		SingleWriter = false,
		SingleReader = true
	};

	public string? LastDispatchedNode { get; private set; }
	/// <summary>Alias of the model that built the most recent prefill (M-Perf.9 #289).</summary>
	public string? LastDispatchedModel { get; private set; }
	/// <summary>SHA-256 hex of the model that built the most recent prefill (M-Perf.9 #289).</summary>
	public string? LastDispatchedModelHash { get; private set; }

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

		if (config.EnableChunks)
		{
			ChunkEngine.CHUNK_SIZE = config.ChunkSize;
			ChunkConstants.ChunkSize = config.ChunkSize;
		}
		_mainQueue = Channel.CreateBounded<WorkItem>(ChannelOpts(500));
		_prefillQueue = Channel.CreateBounded<WorkItem>(ChannelOpts(50));
		_decodeQueue = Channel.CreateBounded<WorkItem>(ChannelOpts(100));

		log.Information("Scheduler init: workers={Workers} prefiller={Prefill} decoders={Decode} mix={Mix}",
			string.Join(",", config.Workers.Select(w => w.Name)),
			config.Workers.Count(w => w.CanPrefill),
			config.Workers.Count(w => w.CanDecode),
			config.MixPrecisionEnabled);
	}

	public async Task<object> SubmitAsync(
		Dictionary<string, object> request,
		List<Dictionary<string, object>> messages,
		string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct)
	{
		var traceId = Router.NewTraceId();
		var item = new WorkItem(request, messages, sessionId, traceId, prefixHash, estimatedTokens, maxTokens);
		item.HttpCancellationToken = ct;

		_log.Information("request_received Sid={Sid} Stream={Stream}", sessionId, item.IsStreaming);

		await _mainQueue.Writer.WriteAsync(item, ct);

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
		try
		{
			// Streaming: return the chunk enumerable as soon as decode phase produces it
			if (item.IsStreaming)
			{
				return await item.StreamCompletion.Task.WaitAsync(TimeSpan.FromSeconds(600), linked.Token);
			}
			else
			{
				// Non-streaming: wait for full response
				return (await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1800), linked.Token))!;
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

		// Issue #306 / C5 fix: MigrationLatency was defined but never
		// observed. Wrap the body in a stopwatch and observe the elapsed
		// seconds on the success path. The pre-check throw above is a
		// precondition (no migration happened) so it is intentionally not
		// observed — only successful migrations land in the histogram.
		var sw = System.Diagnostics.Stopwatch.StartNew();

		var storeKey = $"{sessionId}.kv";
		var storeResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Get,
			storeKey, ReadOnlyMemory<byte>.Empty, traceId, ct);

		if (storeResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			throw new InvalidOperationException($"Store Get failed for migration: {storeResp.Meta}");

		var slotId = 0;
		var llamaRpc = GetLlamaRpcClient(targetWorker);
		var putResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StatePut,
			slotId.ToString(), storeResp.Payload, traceId, ct);

		var nPastAfter = 0;
		if (putResp.Meta != null)
		{
			var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(putResp.Meta);
			nPastAfter = meta?.TryGetValue("n_past", out var n) == true ? n.GetInt32() : 0;
		}

		_ledger.Register(sessionId, targetNodeName, 0, nPastAfter, entry.PrefixHash);
		sw.Stop();
		CoordinatorMetrics.MigrationLatency.WithLabels(fromNode, targetNodeName).Observe(sw.Elapsed.TotalSeconds);
		_log.Information("migrate_done Sid={Sid} To={Node} NPast={N} LatencyS={L:F3}",
			sessionId, targetNodeName, nPastAfter, sw.Elapsed.TotalSeconds);

		return new { migrated = true, session_id = sessionId, target = targetNodeName, n_past = nPastAfter };
	}

	public async Task RunAsync(CancellationToken ct)
	{
		var prefillslots = Math.Max(1, _cfg.Workers.Count(w => w.CanPrefill));
		var decodeSlots = Math.Max(1, _cfg.Workers.Count(w => w.CanDecode));

		_ = ReportQueueDepthAsync(ct);

		var tasks = new[]
		{
			RunClassifierAsync(_cfg.Workers.Count, ct),
			RunPrefillConsumerAsync(prefillslots, ct),
			RunDecodeConsumerAsync(decodeSlots, ct),
		};
		await Task.WhenAll(tasks);
	}

	private async Task ReportQueueDepthAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			CoordinatorMetrics.MainQueueDepth.Set(_mainQueue.Reader.Count);
			CoordinatorMetrics.PrefillQueueDepth.Set(_prefillQueue.Reader.Count);
			CoordinatorMetrics.DecodeQueueDepth.Set(_decodeQueue.Reader.Count);
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

	// ── State helpers for queue routing ──

	private static bool IsPrefillPipeline(WorkItemState s) => s switch
	{
		WorkItemState.ModelLoadPrefill or
		WorkItemState.PrefixRestore or
		WorkItemState.Prefill or
		WorkItemState.SaveKv or
		WorkItemState.SaveDone or
		WorkItemState.MarkEvicted => true,
		_ => false,
	};

	// ── Classifier: reads from main queue, runs RouteAsync, routes to role queue ──

	private async Task RunClassifierAsync(int concurrency, CancellationToken ct)
	{
		var sem = new SemaphoreSlim(concurrency, concurrency);

		await foreach (var item in _mainQueue.Reader.ReadAllAsync(ct))
		{
			await sem.WaitAsync(ct);

			var scope = _serviceProvider.CreateScope();
			_ = Task.Run(async () =>
			{
				try
				{
					await ClassifyItemAsync(item, ct);
				}
				finally
				{
					scope.Dispose();
					sem.Release();
				}
			}, ct);
		}
	}

	private async Task ClassifyItemAsync(WorkItem item, CancellationToken ct)
	{
		try
		{
			item.RecordPhase("queue_wait_ms");
			CoordinatorMetrics.QueueWaitDuration.Observe(item.Phases["queue_wait_ms"] / 1000.0);
			// Issue #306: surface the dequeued head's age as a gauge so the
			// bench suite can detect head-of-line blocking. The histogram
			// above captures the distribution; this gauge gives a current
			// reading for Grafana single-stat panels.
			CoordinatorMetrics.QueueHeadAge.Set(item.Phases["queue_wait_ms"] / 1000.0);
			var next = await DispatchAsync(item, ct);
			if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
			{
				await FinalizeAsync(item, next);
				return;
			}

			if (next == WorkItemState.None)
			{
				_log.Error("classifier_no_worker Sid={Sid} — cannot route, no worker available",
					item.SessionId);
				item.Error = new InvalidOperationException("No worker available for classification");
				await FinalizeAsync(item, WorkItemState.Failed);
				return;
			}

			item.State = next;
			EmitPartialTimeline(item, next is WorkItemState.Decode or WorkItemState.ModelLoadDecode ? "decoding" : next is WorkItemState.Prefill or WorkItemState.ModelLoadPrefill ? "prefilling" : "routed");
			_log.Information("state_transition Sid={Sid} None->{Next} ms={Ms}", item.SessionId, next, item.ElapsedMs);

			if (IsPrefillPipeline(next))
				await _prefillQueue.Writer.WriteAsync(item, ct);
			else
				await _decodeQueue.Writer.WriteAsync(item, ct);
		}
		catch (OperationCanceledException)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "classifier_crashed Sid={Sid}", item.SessionId);
			item.Error = ex;
			await FinalizeAsync(item, WorkItemState.Failed);
		}
	}

	// ── Prefill consumer: processes prefill states until handoff to decode queue ──

	private async Task RunPrefillConsumerAsync(int concurrency, CancellationToken ct)
	{
		var sem = new SemaphoreSlim(concurrency, concurrency);

		await foreach (var item in _prefillQueue.Reader.ReadAllAsync(ct))
		{
			await sem.WaitAsync(ct);

			var scope = _serviceProvider.CreateScope();
			_ = Task.Run(async () =>
			{
				try
				{
					await RunPrefillPipeline(item, ct);
				}
				finally
				{
					scope.Dispose();
					sem.Release();
				}
			}, ct);
		}
	}

	private async Task RunPrefillPipeline(WorkItem item, CancellationToken ct)
	{
		try
		{
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
					// Prefill done — hand off to decode queue. Decode consumer will retry.
					await _decodeQueue.Writer.WriteAsync(item, ct);
					return;
				}

				var prev = item.State;
				item.State = next;
				_log.Information("state_transition Sid={Sid} {Prev}->{Next} ms={Ms}",
					item.SessionId, prev, next, item.ElapsedMs);

				// Handoff boundary: next state is decode-side → hand to decode queue
				if (!IsPrefillPipeline(next))
				{
					await _decodeQueue.Writer.WriteAsync(item, ct);
					return;
				}
			}
		}
		catch (OperationCanceledException)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "prefill_crashed State={State}", item.State);
			item.Error = ex;
			await FinalizeAsync(item, WorkItemState.Failed);
		}
	}

	// ── Decode consumer: processes decode states until terminal ──

	private async Task RunDecodeConsumerAsync(int concurrency, CancellationToken ct)
	{
		var sem = new SemaphoreSlim(concurrency, concurrency);

		await foreach (var item in _decodeQueue.Reader.ReadAllAsync(ct))
		{
			await sem.WaitAsync(ct);

			var scope = _serviceProvider.CreateScope();
			_ = Task.Run(async () =>
			{
				try
				{
					await RunDecodePipeline(item, ct);
				}
				finally
				{
					scope.Dispose();
					sem.Release();
				}
			}, ct);
		}
	}

	private async Task RunDecodePipeline(WorkItem item, CancellationToken ct)
	{
		try
		{
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
					await _decodeQueue.Writer.WriteAsync(item, ct);
					return;
				}

				var prev = item.State;
				item.State = next;
				_log.Information("state_transition Sid={Sid} {Prev}->{Next} ms={Ms}",
					item.SessionId, prev, next, item.ElapsedMs);
			}
		}
		catch (OperationCanceledException)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "decode_crashed State={State}", item.State);
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
						_log.Warning("verify_warm_slot_failed Sid={Sid} Slot={Slot}",
							item.SessionId, entry.SlotId);
						await SaveSlotStateBeforeEvictAsync(item.SessionId, item.DecodeWorker!.Name, item.DecodeSlot ?? 0, item.TraceId, default);
						await item.DecodeLease.DisposeAsync();
						item.DecodeLease = null;
						_ledger.MarkEvicted(item.SessionId);
						item.State = WorkItemState.PickDecode;
						return await PickDecodeAsync(item);
					}
				}

				// N-past guard: estimated tokens too small, force KV restore
				if (entry.NPast > 0 && entry.NPast > _cfg.AtomicThreshold * 4
					&& item.EstimatedTokens < entry.NPast * _cfg.NPastGuardThreshold)
				{
					_log.Warning("n_past_guard Sid={Sid} NPast={N} Est={E}",
						item.SessionId, entry.NPast, item.EstimatedTokens);
					_ledger.UpdateNPast(item.SessionId, 0);
					await SaveSlotStateBeforeEvictAsync(item.SessionId, item.DecodeWorker!.Name, item.DecodeSlot ?? 0, item.TraceId, default);
					_ledger.MarkEvicted(item.SessionId);
					await item.DecodeLease.DisposeAsync();
					item.DecodeLease = null;
					item.State = WorkItemState.PickDecode;
					return await PickDecodeAsync(item);
				}

				_ledger.UpdateLastUsed(item.SessionId);
				return _cfg.MixPrecisionEnabled && Router.DecodeModel(target) != null
					? WorkItemState.ModelLoadDecode
					: WorkItemState.Decode;
			}

			// Affinity worker busy — try cross-node (Gap 6)
			var alt = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health,
				exclude: entry.NodeName);
			if (alt != null && _tracker.TryAcquireSlot(alt.Name, out var altSlot, "decode"))
			{
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

	private async Task<WorkItemState> EvictWarmAndColdRouteAsync(WorkItem item)
	{
		if (_warmLeases.TryRemove(item.SessionId, out var warmLease))
		{
			await SaveSlotStateBeforeEvictAsync(item.SessionId, warmLease.WorkerName, warmLease.SlotId, item.TraceId, default);
			await warmLease.DisposeAsync();
			_decodeSlotSignal.Release();
		}
		_ledger.MarkEvicted(item.SessionId);
		item.State = WorkItemState.RouteDecision;
		return await ColdRouteAsync(item);
	}

	private async Task<WorkItemState> ColdRouteAsync(WorkItem item)
	{
		// Two-engine "work together": a large request may recruit a second engine.
		// The head runs prefill+decode (engine mode handles both internally) with the
		// peer attached; if activation fails at decode time we transparently fall back
		// to solo. Gated behind PipelineEnabled/CombinedEnabled (default off).
		var mePlan = MultiEngineRouter.Select(_cfg, _cfg.Workers, _tracker, _health, item.EstimatedTokens);
		if (mePlan is { } plan && TryAcquireMultiEngine(item, plan))
			return WorkItemState.Decode;

		// Cold route: no warm slot/cache to reuse — the chosen worker prefills the
		// full prompt. Gate the single-worker atomic route on the prompt size only
		// (output is ignored). Warm follow-ups are handled in RouteAsync / migration.
		bool atomic = _cfg.RunMode == "fast" || item.EstimatedTokens <= _cfg.AtomicThreshold;

		if (atomic)
		{
			var aw = Router.PickBestAtomicWorker(_cfg.Workers, _tracker, _health);
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
				// In engine mode, model is loaded at startup — skip ModelLoadDecode
				return _cfg.UseLlamaEngine ? WorkItemState.Decode : WorkItemState.ModelLoadDecode;
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
			_decodeSlotSignal.Release();
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
	/// Reserve both the head slot and the peer slot for a multi-engine request, and stamp the
	/// plan onto the item. "Ensure both engine workers free" — if either acquisition fails the
	/// other is released and we return false so the caller falls back to the normal route.
	/// </summary>
	private bool TryAcquireMultiEngine(WorkItem item, MultiEngineRouter.Plan plan)
	{
		if (!_tracker.TryAcquireSlot(plan.Head.Name, out var headSlot, "decode"))
			return false;
		if (!_tracker.TryAcquireSlot(plan.Peer.Name, out var peerSlot, "decode"))
		{
			_tracker.ReleaseSlot(plan.Head.Name, headSlot);
			return false;
		}

		var modeStr = ModeLabel(plan.Mode);
		item.RouteType = $"cold_{modeStr}";
		item.MultiMode = plan.Mode;
		item.MultiPeer = plan.Peer.Name;
		item.MultiSplit = plan.OtSplit;
		item.DecodeWorker = plan.Head;
		item.DecodeSlot = headSlot;
		item.DecodeLease = new SlotLease(plan.Head.Name, headSlot, item.SessionId, LeaseLifetime.Long, _tracker);
		item.PeerLease = new SlotLease(plan.Peer.Name, peerSlot, item.SessionId, LeaseLifetime.Long, _tracker);
		LastDispatchedNode = plan.Head.Name;

		CoordinatorMetrics.RequestsTotal.WithLabels(plan.Head.Name, item.RouteType).Inc();
		CoordinatorMetrics.RequestsTotalAll.Inc();
		CoordinatorMetrics.ColdSessionStarts.Inc();
		CoordinatorMetrics.MultiEngineAttempts.WithLabels(plan.Head.Name, modeStr).Inc();
		_log.Information("multiengine_route Sid={Sid} Mode={Mode} Head={Head} HeadSlot={HS} Peer={Peer} PeerSlot={PS} Split={Split} Est={Est}",
			item.SessionId, modeStr, plan.Head.Name, headSlot, plan.Peer.Name, peerSlot, plan.OtSplit, item.EstimatedTokens);
		return true;
	}

	/// <summary>
	/// Activate the chosen multi-engine mode on the head just before decode. COMBINED flips the
	/// MoE expert tensors onto the peer's RPC backend (EngineSetExpertMode); PIPELINE attaches the
	/// peer and hands it its --override-tensor split (EnginePipelineAttach). Any failure — RPC
	/// error, or the engine reporting it stayed solo — degrades transparently to solo decode.
	/// </summary>
	private async Task ApplyMultiEngineAsync(WorkItem item, WorkerConfig head, int slotId, CancellationToken ct)
	{
		if (item.MultiMode == MultiEngineMode.None) return;
		var modeStr = ModeLabel(item.MultiMode);
		var llamaRpc = GetLlamaRpcClient(head);
		try
		{
			Hydra.Shared.RpcResponse resp;
			if (item.MultiMode == MultiEngineMode.Combined)
			{
				resp = await llamaRpc.EngineSetExpertModeAsync(slotId.ToString(), "combined", item.TraceId, ct);
			}
			else
			{
				var addr = !string.IsNullOrWhiteSpace(head.PeerHost)
					? $"{head.PeerHost}:{head.PeerPort}"
					: ResolvePeerAddr(item.MultiPeer);
				resp = await llamaRpc.EnginePipelineAttachAsync(slotId.ToString(), addr, item.MultiSplit ?? "", item.TraceId, ct);
			}

			if (resp.Status == (byte)StatusCode.Ok && !ReportsSolo(resp.Meta))
			{
				CoordinatorMetrics.MultiEngineActive.WithLabels(head.Name, modeStr).Inc();
				CoordinatorMetrics.MultiEngineActiveSessions.WithLabels(modeStr).Inc();
				CoordinatorMetrics.EnginePeerUp.WithLabels(head.Name, item.MultiPeer ?? "").Set(1);
				_activeMultiSessions[item.SessionId] = modeStr;
				_log.Information("multiengine_active Sid={Sid} Mode={Mode} Head={Head} Peer={Peer}",
					item.SessionId, modeStr, head.Name, item.MultiPeer);
			}
			else
			{
				item.MultiFellBack = true;
				CoordinatorMetrics.MultiEngineFallback.WithLabels(head.Name, modeStr, "peer_declined").Inc();
				CoordinatorMetrics.EnginePeerUp.WithLabels(head.Name, item.MultiPeer ?? "").Set(0);
				_log.Warning("multiengine_fallback Sid={Sid} Mode={Mode} Status={St} Meta={Meta}",
					item.SessionId, modeStr, resp.Status, resp.Meta);
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

	private async Task ReleasePeerLeaseAsync(string sessionId, SlotLease lease)
	{
		await lease.DisposeAsync();
		if (_activeMultiSessions.TryRemove(sessionId, out var modeStr))
			CoordinatorMetrics.MultiEngineActiveSessions.WithLabels(modeStr).Dec();
	}

	private static string ModeLabel(MultiEngineMode mode) =>
		mode == MultiEngineMode.Pipeline ? "pipeline" : "combined";

	/// <summary>Working-together status surfaced on the response (and in /status) for observability.</summary>
	internal static Dictionary<string, object> MultiEngineStatus(WorkItem item) => new()
	{
		["engine_mode"] = item.MultiFellBack ? "solo" : ModeLabel(item.MultiMode),
		["requested_mode"] = ModeLabel(item.MultiMode),
		["peer"] = item.MultiPeer ?? "",
		["split"] = item.MultiSplit ?? "",
		["fell_back"] = item.MultiFellBack
	};

	private async Task<WorkItemState> ModelLoadAsync(WorkItem item)
	{
		if (_cfg.MixPrecisionEnabled)
		{
			var w = item.State == WorkItemState.ModelLoadPrefill ? item.PrefillWorker! : item.DecodeWorker!;
			var m = item.State == WorkItemState.ModelLoadPrefill ? Router.PrefillModel(w) : Router.DecodeModel(w);
			if (m != null)
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();
				var ok = await _proxy.LoadModelAsync(w.LlamaUrl, m, item.TraceId, CancellationToken.None);
				sw.Stop();
				if (ok)
					_log.Information("model_loaded Model={M} Worker={W} DurationMs={Ms}", m, w.Name, sw.ElapsedMilliseconds);
				else
					_log.Warning("model_load_failed Model={M} Worker={W} DurationMs={Ms}", m, w.Name, sw.ElapsedMilliseconds);
				CoordinatorMetrics.ModelLoadDuration.Observe(sw.Elapsed.TotalSeconds);
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

			var slotId = item.PrefillSlot ?? 0;
			var llamaRpc = GetLlamaRpcClient(item.PrefillWorker);
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
	private async Task<WorkItemState> PrefillAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var engineFailed = false;
		string? engineFailReason = null;

		if (_cfg.UseLlamaEngine)
		{
			// Engine mode: use EnginePrefill RPC which tokenizes internally.
			// Defensive fallback (#279): if the engine RPC fails — e.g., the
			// deployed llama-server binary is older than the C# integration
			// and doesn't know opcode 0x42, or any future regression — fall
			// through to the HTTP path below instead of returning 503. The
			// HTTP path uses the same OAI body and slot, so the user-visible
			// behavior is identical except for a slightly higher prefill
			// latency (HTTP JSON overhead vs the binary engine RPC).
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
				// M-Perf.9 #289: include the prefill model alias so the engine
				// can swap to it (or fall back to the resident model if the
				// alias is unknown / no preset is configured). When null, the
				// engine uses the current resident model — pre-feature behavior.
				var prefillModel = Router.PrefillModel(w);
				if (prefillModel != null)
					body["model"] = prefillModel;
				if (item.PrefillSlot == null)
					item.PrefillSlot = slotId;
				var requestJson = JsonSerializer.Serialize(body);
				var llamaRpc = GetLlamaRpcClient(w);
				var engine = new HydraEngineClient(llamaRpc);
				var prefillResult = await engine.EnginePrefillAsync(slotId, prefillModel, requestJson, item.TraceId, ct);
				if (prefillResult == null)
					throw new InvalidOperationException("EnginePrefill returned no result");

				item.NPastAfter = prefillResult.NPast;
				item.KvBlob = prefillResult.KvBlob; // KV state blob for SaveKv

				// M-Perf.9 #289: capture the model identity the engine actually
				// used. These ride item.KvModelAlias/Hash/Path/Fallback into the
				// request_timeline log line, the Store manifest (via the session
				// ledger), and the RestoreKvAsync cross-model guard below.
				item.KvModelAlias    = prefillResult.ModelAlias;
				item.KvModelHash     = prefillResult.ModelHash;
				item.KvModelPath     = prefillResult.ModelPath;
				item.KvModelFallback = prefillResult.ModelFallback;
				LastDispatchedModel     = item.KvModelAlias;
				LastDispatchedModelHash = item.KvModelHash;
				if (item.KvModelFallback && prefillModel != null)
				{
					CoordinatorMetrics.ModelFallbackTotal
						.WithLabels(w.Name, prefillModel).Inc();
				}

				_log.Information("prefill_done Sid={Sid} Node={Node} Slot={Slot} NPastFromEngine={N} EstTokens={Est} Model={Model} Fallback={Fb}",
					item.SessionId, w.Name, slotId, item.NPastAfter, item.EstimatedTokens,
					item.KvModelAlias ?? "?", item.KvModelFallback);
				if (item.NPastAfter > 0)
				{
					_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
					if (item.PrefillSlot == null || item.PrefillSlot == 0)
						ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Engine RPC failed — fall through to the HTTP path below.
				// The HTTP path uses the same slot (item.PrefillSlot, already
				// acquired in ColdRouteAsync) and the same OAI body
				// (item.Request). The HTTP prefill on the same slot either
				// reuses the prefix cache or re-prefills from scratch —
				// either way the slot ends up with the right KV state for
				// SaveKv → migration → decode.
				//
				// Correctness note: the fallback's SaveKv path (called after
				// the HTTP prefill succeeds) uses the StateGet RPC opcode
				// (0x40), which the older binary DOES support — the 2-commit
				// gap behind #279 only added the 0x42 (EnginePrefill) dispatch,
				// not the 0x40 (StateGet) one. So in the specific #279 scenario,
				// the save path is unaffected by the same binary mismatch. If
				// a future gap ever covers StateGet, this fallback's correctness
				// degrades and we need to revisit.
				engineFailed = true;
				engineFailReason = ex.Message;
				item.KvBlob = null; // engine didn't produce a blob
				CoordinatorMetrics.EnginePrefillFallbacks
					.WithLabels(w.Name, "engine_rpc_error")
					.Inc();
				_log.Warning(ex,
					"engine_prefill_fell_back_to_http Sid={Sid} Worker={W} Slot={Slot} Reason={Reason}",
					item.SessionId, w.Name, item.PrefillSlot, engineFailReason);
			}
		}

		// HTTP path — taken when:
		//   - Legacy non-engine mode (_cfg.UseLlamaEngine = false), OR
		//   - Engine mode but the engine RPC failed above (engineFailed = true).
		// In both cases the path is identical: send an OAI chat-completion
		// with n_predict=0 to the llama-server, which will tokenize the
		// prompt, reuse the slot's prefix cache, and prefill the rest.
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
			// The HTTP path can't learn the model_hash from the response
			// (the OAI completion response doesn't carry it), so we query the
			// slot META. The META call also confirms the slot's n_past and
			// surfaces the engine's model_alias/model_hash/model_path fields
			// (when the server supports them).
			item.KvModelAlias = prefillModel;
			try
			{
				var slotMeta = await GetLlamaClient(w).GetStateMetaAsync(item.PrefillSlot ?? 0, ct);
				if (!string.IsNullOrEmpty(slotMeta.ModelAlias))
					item.KvModelAlias = slotMeta.ModelAlias;
				if (!string.IsNullOrEmpty(slotMeta.ModelHash))
					item.KvModelHash = slotMeta.ModelHash;
				if (!string.IsNullOrEmpty(slotMeta.ModelPath))
					item.KvModelPath = slotMeta.ModelPath;
			}
			catch (Exception ex)
			{
				// Non-fatal: the cross-model guard will skip the check
				// (both hashes empty) if we couldn't query META. Logged at
				// Warning for parity with cross_model_check_failed (P2.10
				// consistency) — META failures are a real signal in Loki
				// (operator can spot pre-#289 binaries or transient issues).
				_log.Warning(ex, "prefill_meta_query_failed Slot={Slot}", item.PrefillSlot);
			}
			LastDispatchedModel     = item.KvModelAlias;
			LastDispatchedModelHash = item.KvModelHash;

			_log.Information("prefill_done Sid={Sid} Node={Node} Slot={Slot} NPastFromLLama={N} EstTokens={Est} ViaHttp={Http} Model={Model}",
				item.SessionId, w.Name, item.PrefillSlot, item.NPastAfter, item.EstimatedTokens, engineFailed,
				item.KvModelAlias ?? "?");
			if (item.NPastAfter > 0)
			{
				_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
				if (item.PrefillSlot == null || item.PrefillSlot == 0)
					ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
			}
		}

		CoordinatorMetrics.PrefillDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("prefill_ms") / 1000.0);
		return WorkItemState.SaveKv;
	}

	private async Task<WorkItemState> SaveKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var slotId = item.PrefillSlot ?? 0;
		_log.Information("save_kv_start Sid={Sid} Slot={Slot} NPast={N} Node={Node}",
			item.SessionId, slotId, item.NPastAfter, w.Name);
		try
		{
			// In engine mode, the KV blob is already in item.KvBlob from EnginePrefill
			byte[]? payload;
			if (_cfg.UseLlamaEngine && item.KvBlob != null)
			{
				payload = item.KvBlob;
				item.KvBlob = null; // Free memory early
				// Save to store (same logic as SaveKvStateCoreAsync)
				var storeKey = $"{item.SessionId}.kv";
				if (_cfg.EnableChunks)
				{
					var chunks = ChunkEngine.ChunkAndHash(payload);
					var orderedHashes = chunks.Select(c => c.Hash).ToList();
					var missing = await SyncMissingAsync(storeKey, orderedHashes, item.TraceId, ct);
					await PushMissingChunksAsync(storeKey, item.SessionId, missing, chunks, payload, item.TraceId, ct);
					// M-Perf.9 #289: persist model identity alongside the KV so
					// the cross-model guard in RestoreKvAsync can detect a model
					// swap between prefill and decode (e.g. Mini prefill → Balanced
					// decode would otherwise silently corrupt the response).
					await PutManifestAsync(
						storeKey, item.NPastAfter, payload.Length, chunks,
						item.TraceId, ct,
						item.KvModelAlias ?? "", item.KvModelHash ?? "", item.KvModelPath ?? "");
					if (_chunkCache != null)
					{
						await _chunkCache.SaveHashesAsync(item.SessionId, orderedHashes, ct);
						foreach (var c in chunks)
							await _chunkCache.SaveChunkDataAsync(item.SessionId, c.Hash,
								payload.AsSpan(c.Index * _cfg.ChunkSize, Math.Min(_cfg.ChunkSize, (int)(payload.Length - c.Index * _cfg.ChunkSize))).ToArray(), ct);
					}
				}
				else
				{
					await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
						storeKey, payload, item.TraceId, ct);
				}
			}
			else
			{
				payload = await SaveKvStateCoreAsync(w, slotId, item.SessionId, item.NPastAfter, item.TraceId, ct);
			}

			if (payload == null)
				throw new InvalidOperationException($"StateGet RPC failed for save");

			item.KvBytes = payload.Length;
			var entry = _ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter, item.PrefixHash);
			lock (entry) { entry.HasStoreState = true; }
			item.Entry = entry;
			_log.Information("state_saved Sid={Sid} SizeMB={Size}", item.SessionId, payload.Length / 1024 / 1024);

			if (item.PrefixHash != null && _cfg.PrefixCheckpointEnabled)
			{
				var prefixKey = $"prefix/{item.PrefixHash}.kv";
				var kvPayload = payload;
				var traceId = item.TraceId;
				_ = Task.Run(async () =>
				{
					try
					{
						var stat = await StoreClient.RequestAsync(Hydra.Shared.OpCode.Stat,
							prefixKey, ReadOnlyMemory<byte>.Empty, traceId, CancellationToken.None);
						if (stat.Status != (byte)Hydra.Shared.StatusCode.Ok)
						{
							await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
								prefixKey, kvPayload, traceId, CancellationToken.None);
							CoordinatorMetrics.PrefixSaves.Inc();
							_log.Information("prefix_saved Hash={Hash} SizeMB={Size}",
								item.PrefixHash, kvPayload.Length / 1024 / 1024);
						}
					}
					catch (Exception ex)
					{
						_log.Warning(ex, "prefix_save_failed Hash={Hash}", item.PrefixHash);
					}
				});
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "save_failed_fallback Sid={Sid} — falling back to same-node decode", item.SessionId);
			if (item.Entry != null) { lock (item.Entry) { item.Entry.HasStoreState = false; } }
			if (item.DecodeLease != null) { await item.DecodeLease.DisposeAsync(); item.DecodeLease = null; _decodeSlotSignal.Release(); }
			if (item.PrefillWorker?.CanDecode == true
				&& _tracker.TryAcquireSlot(item.PrefillWorker.Name, out var fbSlot, "decode-fallback"))
			{
				item.DecodeWorker = item.PrefillWorker;
				item.DecodeSlot = fbSlot;
				item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				_log.Information("save_fallback_decode Sid={Sid} Node={Node} Slot={Slot}",
					item.SessionId, item.PrefillWorker.Name, fbSlot);
				CoordinatorMetrics.SaveKvDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(item.RecordPhase("save_kv_ms") / 1000.0);
				return WorkItemState.Decode;
			}
			_log.Error("save_fallback_no_slot Sid={Sid} — prefill node has no free decode slot", item.SessionId);
			return WorkItemState.Failed;
		}
		CoordinatorMetrics.SaveKvDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("save_kv_ms") / 1000.0);
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
		var dw = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health,
			item.PrefillWorker?.Name)
			?? (item.PrefillWorker?.CanDecode == true
				? item.PrefillWorker : null);

		if (dw == null)
			return WorkItemState.None;

		if (_tracker.TryAcquireSlot(dw.Name, out var slot, "decode"))
		{
			item.DecodeWorker = dw;
			item.DecodeSlot = slot;
			item.DecodeLease = new SlotLease(dw.Name, slot, item.SessionId,
				LeaseLifetime.Long, _tracker);
			LastDispatchedNode = dw.Name;

			// Same-node skip: when decode == prefill and no model switch,
			// the KV state is already on the node — no restore needed.
			//
			// M-Perf.9 #289: the alias-equality check is necessary but not
			// sufficient. The operator can swap the GGUF file behind a
			// stable alias (e.g. rebuild Balanced.gguf on disk) — the alias
			// stays "balanced" but the model_hash changes. When the slot
			// carries a different model_hash from the KV the prefill built,
			// we must NOT skip — fall through to restore so the cross-model
			// guard in RestoreKvAsync can catch it.
			if (item.PrefillWorker?.Name == dw.Name
				&& (!_cfg.MixPrecisionEnabled
					|| Router.DecodeModel(dw) == null
					|| Router.DecodeModel(dw) == Router.PrefillModel(item.PrefillWorker!)))
			{
				// Alias says same; verify the model_hash actually matches.
				// Both-empty (pre-#289 or no metadata) skips the hash check
				// for back-compat — falls back to the old alias-only skip.
				bool aliasSaysSame = Router.DecodeModel(dw) == null
					|| Router.DecodeModel(dw) == Router.PrefillModel(item.PrefillWorker!);
				bool canCheckHash = !string.IsNullOrEmpty(item.KvModelHash);
				if (!aliasSaysSame || !canCheckHash)
				{
					_log.Information("same_node_skip Sid={Sid} Node={Node} — KV already resident (alias check)",
						item.SessionId, dw.Name);
					return WorkItemState.Decode;
				}

				try
				{
					// Item.PrefillSlot is the slot the prefill wrote to; same
					// worker, possibly same slot. Query its META to read the
					// current resident model_hash.
					var prefillSlotId = item.PrefillSlot ?? slot;
					// CancellationToken.None: the META query is best-effort and
					// the try-catch below swallows failures. Plumbing ct
					// through PickDecodeAsync would cascade to 5+ call sites
					// and the next-step state machine for a non-critical read.
					var slotMeta = await GetLlamaClient(dw).GetStateMetaAsync(prefillSlotId, default);
					if (string.IsNullOrEmpty(slotMeta.ModelHash)
						|| string.Equals(slotMeta.ModelHash, item.KvModelHash, StringComparison.Ordinal))
					{
						_log.Information("same_node_skip Sid={Sid} Node={Node} Slot={Slot} — KV already resident (hash match)",
							item.SessionId, dw.Name, prefillSlotId);
						return WorkItemState.Decode;
					}
					_log.Information("same_node_skip_hash_mismatch Sid={Sid} Node={Node} Slot={Slot} stored={Stored} resident={Resident} — falling through to restore for cross-model guard",
						item.SessionId, dw.Name, prefillSlotId, item.KvModelHash, slotMeta.ModelHash);
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

	private async Task<WorkItemState> RestoreKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		var entry = _ledger.Lookup(item.SessionId);
		var slotId = Math.Min(
			item.DecodeSlot ?? item.PrefillSlot ?? entry?.SlotId ?? 0,
			w.Slots - 1);
		item.DecodeSlot = slotId; // Sync clamped slot so DecodeAsync pins the same one
		var storeKey = $"{item.SessionId}.kv";
		_log.Information("restore_kv_start Sid={Sid} Key={Key} Node={Node} Slot={Slot}",
			item.SessionId, storeKey, w.Name, slotId);
		try
		{
			byte[] restoreBlob;
			if (_cfg.EnableChunks)
			{
				// Chunked restore: get manifest, assemble from local cache + store, StatePut
				var manifestResp = await StoreClient.RequestAsync(Hydra.Shared.OpCode.GetManifest,
					storeKey, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
				if (manifestResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
					throw new InvalidOperationException($"GetManifest failed: status={manifestResp.Status} meta={manifestResp.Meta}");

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
				// M-Perf.9 #289: read the model identity of the slot that built
				// this KV. Pre-#289 manifests (saved before this field existed)
				// don't carry the keys — TryGetProperty returns false and the
				// fields stay "", which the cross-model guard treats as "skip"
				// (back-compat with old data). The model identity is needed
				// across a Coordinator restart when the prefill is no longer
				// in memory but the manifest in PG still carries it.
				if (manifestRoot.TryGetProperty("model_alias", out var ma))
					item.KvModelAlias = ma.GetString();
				if (manifestRoot.TryGetProperty("model_hash", out var mh))
					item.KvModelHash = mh.GetString();
				if (manifestRoot.TryGetProperty("model_path", out var mp))
					item.KvModelPath = mp.GetString();
				if (item.NPastAfter > 0) nPast = item.NPastAfter;
				else item.NPastAfter = nPast;

				restoreBlob = await AssembleFromChunksAsync(null, storeKey, manifestChunks, item.TraceId, ct);
				item.KvBytes = restoreBlob.Length;
				_log.Information("state_assembled Sid={Sid} SizeMB={Size} Chunks={Count}",
					item.SessionId, restoreBlob.Length / 1024 / 1024, manifestChunks.Count);
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

			var llamaRpc = GetLlamaRpcClient(w);
			var putResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StatePut,
				slotId.ToString(), restoreBlob, item.TraceId, ct);

			if (putResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
				throw new InvalidOperationException($"StatePut RPC failed: status={putResp.Status} meta={putResp.Meta}");

			if (putResp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(putResp.Meta);
				item.NPastAfter = meta?.TryGetValue("n_past", out var n) == true ? n.GetInt32() : item.NPastAfter;
			}

			// M-Perf.9 #289: cross-model safety check. After the StatePut
			// succeeded, query the slot META to learn what model is currently
			// resident on the slot, then compare its model_hash to the
			// stored KV's model_hash (carried on item.KvModelHash from the
			// prefill that built it). If they differ, the restored KV would
			// be decoded by a different model — silently corrupt. Default
			// behaviour: abort the restore, erase the slot, and re-prefill on
			// the correct model. When AllowCrossModelKvReuse=true: warn and
			// proceed (the model will likely reject the KV at decode time).
			//
			// The decision is delegated to CrossModelGuard.Decide (pure
			// function, fully unit-tested). The only IO done here is the
			// slot META query; the guard itself has no side effects.
			try
			{
				var slotMeta = await GetLlamaClient(w).GetStateMetaAsync(slotId, ct);
				var outcome = CrossModelGuard.Decide(
					storedHash:             item.KvModelHash,
					slotHash:               slotMeta.ModelHash,
					allowCrossModelKvReuse: _cfg.AllowCrossModelKvReuse);

				switch (outcome)
				{
					case CrossModelGuard.Outcome.Abort:
						_log.Warning("cross_model_kv_aborted slot={Slot} stored={Stored} slot={SlotHash} item={Item} — re-prefilling",
							slotId, item.KvModelHash, slotMeta.ModelHash, item.SessionId);
						CoordinatorMetrics.CrossModelKvAborted.WithLabels(w.Name).Inc();
						// Erase the slot we just wrote into (otherwise the
						// same-node skip would skip the re-prefill's PREFILL
						// state by trusting the now-incorrect slot).
						try
						{
							await GetLlamaClient(w).EraseSlotAsync(slotId, ct);
						}
						catch (Exception eraseEx)
						{
							_log.Warning(eraseEx, "cross_model_erase_failed Slot={Slot}", slotId);
						}
						if (item.DecodeLease != null)
						{
							await item.DecodeLease.DisposeAsync();
							item.DecodeLease = null;
							_decodeSlotSignal.Release();
						}
						if (item.PrefillWorker?.CanDecode == true
							&& _tracker.TryAcquireSlot(item.PrefillWorker.Name, out var fbSlot2, "decode-fallback"))
						{
							item.DecodeWorker = item.PrefillWorker;
							item.DecodeSlot = fbSlot2;
							item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot2, item.SessionId,
								LeaseLifetime.Long, _tracker);
						}
						item.NPastAfter = 0;
						item.KvRestoredForDecode = false;
						CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
							.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
						return WorkItemState.Prefill;

					case CrossModelGuard.Outcome.WarnAndProceed:
						_log.Warning("cross_model_kv_warn slot={Slot} stored={Stored} slot={SlotHash} — allow_cross_model_kv_reuse=true",
							slotId, item.KvModelHash, slotMeta.ModelHash);
						CoordinatorMetrics.CrossModelKvWarned.WithLabels(w.Name).Inc();
						break;

					case CrossModelGuard.Outcome.Skip:
						_log.Debug("cross_model_kv_skipped_empty slot={Slot} item={Item} — back-compat",
							slotId, item.SessionId);
						CoordinatorMetrics.CrossModelKvSkipped.WithLabels(w.Name).Inc();
						break;

					case CrossModelGuard.Outcome.Proceed:
					default:
						// Hashes match (or both empty AND not Skip — which
						// shouldn't happen, but is a safe no-op). Fall through.
						CoordinatorMetrics.CrossModelKvProceeded.WithLabels(w.Name).Inc();
						break;
				}
			}
			catch (Exception ex)
			{
				// Non-fatal: META query failed. The decode may produce a
				// corrupted response but we don't fail-closed here because
				// the META endpoint may not be available on older builds.
				_log.Warning(ex, "cross_model_check_failed slot={Slot} — proceeding without check", slotId);
			}

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
					_decodeSlotSignal.Release();
				}
				item.DecodeWorker = item.PrefillWorker;
				item.DecodeSlot = fbSlot;
				item.DecodeLease = new SlotLease(item.PrefillWorker.Name, fbSlot, item.SessionId,
					LeaseLifetime.Long, _tracker);
				CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
					.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
				return WorkItemState.Decode;
			}
			_log.Warning(ex, "restore_skipped Sid={Sid} — continuing without KV restore", item.SessionId);
			item.NPastAfter = 0;
			item.KvRestoredForDecode = false;
		}
		if (item.NPastAfter > 0)
			_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
		_ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter > 0 ? item.NPastAfter : entry?.NPast ?? 0, item.PrefixHash);
		CoordinatorMetrics.RestoreKvDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("restore_kv_ms") / 1000.0);
		return WorkItemState.Decode;
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

		// Pin decode to the leased slot so llama-server doesn't pick a different one via LRU
		if (item.DecodeSlot.HasValue)
			item.Request["id_slot"] = item.DecodeSlot.Value;

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

			// Two-engine "work together": activate the peer before decode (no-op when solo).
			if (_cfg.UseLlamaEngine)
				await ApplyMultiEngineAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, cts.Token);

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
		else
		{
			// Two-engine "work together": activate the peer before decode (no-op when solo).
			if (_cfg.UseLlamaEngine)
				await ApplyMultiEngineAsync(item, w, item.DecodeSlot ?? item.LastIdSlot ?? 0, ct);

			// HTTP proxy for chat completions (works for both engine and legacy modes).
			// The engine RPC (EngineDecodeAsync) was previously used here in engine
			// mode, but the RPC payload is just raw bytes — it collapsed the model's
			// `reasoning_content` into `content` and dropped `finish_reason`/`id_slot`/
			// `timings`, making the response unusable for reasoning models like
			// Qwopus3.6-35B-A3B (--reasoning on). The HTTP proxy preserves the full
			// OpenAI schema including `reasoning_content`. The engine RPC is still used
			// for prefill (EnginePrefill) and KV state (StateGet/Put). See issue #273.
			using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(item.HttpCancellationToken, ct);
			var resp = await _proxy.ProxyCompletionAsync(
				w.LlamaUrl, item.Request, item.TraceId, syncCts.Token);
			if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se)
				item.LastIdSlot = se.GetInt32();
			if (item.MultiMode != MultiEngineMode.None)
				resp["hydra"] = MultiEngineStatus(item);
			item.Response = resp;
			item.TokensIn = ExtractUsageInt(resp, "prompt_tokens");
			item.TokensOut = ExtractUsageInt(resp, "completion_tokens");

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
		CoordinatorMetrics.DecodeDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("decode_ms") / 1000.0);
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
		var storeKey = $"{item.SessionId}.kv";

		try
		{
			if (_cfg.UseLlamaEngine && item.KvBlob != null)
			{
				// Engine mode: KV blob already in memory from EngineDecode
				await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
					storeKey, item.KvBlob, item.TraceId, CancellationToken.None);
				item.KvBlob = null;
				_ledger.MarkStoreState(item.SessionId);
				_log.Information("bg_saved Sid={Sid} (engine, KvBlob)", item.SessionId);
			}
			else
			{
				var slotId = item.LastIdSlot ?? 0;
				var llamaRpc = GetLlamaRpcClient(w);
				var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
					slotId.ToString(), ReadOnlyMemory<byte>.Empty, item.TraceId, CancellationToken.None);

				if (stateResp.Status == (byte)Hydra.Shared.StatusCode.Ok)
				{
					await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
						storeKey, stateResp.Payload, item.TraceId, CancellationToken.None);
					_ledger.MarkStoreState(item.SessionId);
					_log.Information("bg_saved Sid={Sid}", item.SessionId);
				}
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
			// Key _streamCompleted by TraceId (per-turn) to avoid stale entries
			// from failed requests leaking into subsequent turns for the same session.
			if (_pendingBgSaves.TryGetValue(sessionId, out var bgInfo) && bgInfo.TraceId is { Length: > 0 } traceId)
				_streamCompleted.TryAdd(traceId, 0);

			// Emit the deferred timeline now that the stream is done — decode_ms/total_ms
			// cover the full stream duration.
			if (_pendingTimelines.TryRemove(sessionId, out var timelineItem))
			{
				FinalizeStreamPhases(timelineItem);
				CoordinatorMetrics.DecodeDuration
					.WithLabels(timelineItem.DecodeWorker?.Name ?? "unknown", RouteLabel(timelineItem))
					.Observe(timelineItem.Phases.GetValueOrDefault("decode_ms") / 1000.0);
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
			if (_pendingBgSaves.TryRemove(sessionId, out var bgInfo2))
			{
				var w = _cfg.Workers.FirstOrDefault(x => x.Name == bgInfo2.WorkerName);
				if (w != null)
				{
					releaseNode = w.Name;
					bgTraceId = bgInfo2.TraceId;
					try
					{
						var llamaRpc = GetLlamaRpcClient(w);
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
				_decodeSlotSignal.Release();
			}
			else
			{
				_log.Warning("stream_done_no_lease Sid={Sid} WarmKeys={Keys}",
					sessionId, string.Join(",", _warmLeases.Keys.Take(5)));
			}

			// Release the peer lease (two-engine) once the stream is fully drained.
			if (_peerLeases.TryRemove(sessionId, out var peerLease))
			{
				peerLease.DisposeAsync().AsTask().ContinueWith(_ => { });
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
				_ = WriteStateToStoreAsync(stateResp.Payload, sessionId, bgTraceId ?? "");
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
				_decodeSlotSignal.Release();
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
	private async Task WriteStateToStoreAsync(byte[] stateBlob, string sessionId, string traceId)
	{
		var saveStart = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
				$"{sessionId}.kv", stateBlob, traceId, CancellationToken.None);
			_ledger.MarkStoreState(sessionId);
			_log.Information("bg_saved Sid={Sid} bytes={Bytes} ms={Ms}",
				sessionId, stateBlob.Length, saveStart.ElapsedMilliseconds);
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
					_decodeSlotSignal.Release();
				}
				else
				{
					// Evict any prior warm lease for this session before stashing the new one.
					// A cross-node fallback turn leaves the old node's lease here; without
					// this guard the old slot is never returned to its pool.
					if (_warmLeases.TryRemove(item.SessionId, out var staleLease))
					{
						await staleLease.DisposeAsync();
						_decodeSlotSignal.Release();
					}
					_warmLeases[item.SessionId] = item.DecodeLease;
				}
			}
			else
			{
				await item.DecodeLease.DisposeAsync();
				_decodeSlotSignal.Release();
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
				_pendingTimelines[item.SessionId] = item;
				// Close the race where the stream completed between the lease check
				// above and the stash: whoever removes the pending entry emits.
				if (_streamCompleted.ContainsKey(item.TraceId)
					&& _pendingTimelines.TryRemove(item.SessionId, out _))
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
			item.Completion.TrySetException(
				item.Error ?? new InvalidOperationException("Failed"));
	}

	// ── Timeline helpers ──

	private static string RouteLabel(WorkItem item) =>
		string.IsNullOrEmpty(item.RouteType) ? "unknown" : item.RouteType;

	/// <summary>Set decode_ms/total_ms for a streaming item once the stream has finished.</summary>
	private static void FinalizeStreamPhases(WorkItem item)
	{
		item.Phases["decode_ms"] = item.ElapsedMs - item.DecodeStartMs;
		item.Phases["total_ms"] = item.ElapsedMs;
	}

	/// <summary>
	/// Emit the per-request phase timeline as a raw logfmt stderr line. Grafana's
	/// timeline dashboard parses this line via extractFields — keep keys stable.
	/// Phase values are per-phase durations (WorkItem.RecordPhase), so they sum
	/// to ≈ total_ms and can be rendered as stacked bars.
	/// </summary>
	private void EmitPartialTimeline(WorkItem item, string status)
	{
		var node = item.PrefillWorker?.Name ?? item.DecodeWorker?.Name ?? "unknown";
		var prefillModel = item.PrefillWorker != null
			? (_health.GetNodeInfo(item.PrefillWorker.Name)?.CurrentModel ?? "")
			: "";
		var decodeModel = item.DecodeWorker != null
			? (_health.GetNodeInfo(item.DecodeWorker.Name)?.CurrentModel ?? "")
			: "";
		Console.Error.WriteLine(
			$"event=request_timeline timestamp_ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
			$"trace_id={item.TraceId} session_id={item.SessionId} " +
			$"queue_wait_ms={item.Phases.GetValueOrDefault("queue_wait_ms")} node={node} " +
			$"route_type={RouteLabel(item)} " +
			$"prefill_node={item.PrefillWorker?.Name ?? "-"} " +
			$"decode_node={item.DecodeWorker?.Name ?? "-"} " +
			$"prefill_model={prefillModel} decode_model={decodeModel} " +
			$"prefill_ms={item.Phases.GetValueOrDefault("prefill_ms")} " +
			$"save_kv_ms={item.Phases.GetValueOrDefault("save_kv_ms")} " +
			$"restore_kv_ms={item.Phases.GetValueOrDefault("restore_kv_ms")} " +
			$"decode_ms={item.Phases.GetValueOrDefault("decode_ms")} " +
			$"tokens_in={item.TokensIn} tokens_out={item.TokensOut} kv_bytes={item.KvBytes} " +
			$"prefix_hit={(item.PrefixCacheHit ? "true" : "false")} " +
			$"status={status}"
		);
	}

	private void EmitTimeline(WorkItem item)
	{
		var node = item.PrefillWorker?.Name ?? item.DecodeWorker?.Name ?? "unknown";
		CoordinatorMetrics.RequestLatency.WithLabels(node, RouteLabel(item))
			.Observe(item.Phases.GetValueOrDefault("total_ms") / 1000.0);
		var prefillModel = item.PrefillWorker != null
			? (_health.GetNodeInfo(item.PrefillWorker.Name)?.CurrentModel ?? "")
			: "";
		var decodeModel = item.DecodeWorker != null
			? (_health.GetNodeInfo(item.DecodeWorker.Name)?.CurrentModel ?? "")
			: "";
		Console.Error.WriteLine(
			$"event=request_timeline timestamp_ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
			$"trace_id={item.TraceId} session_id={item.SessionId} " +
			$"queue_wait_ms={item.Phases.GetValueOrDefault("queue_wait_ms")} node={node} " +
			$"route_type={RouteLabel(item)} " +
			$"prefill_node={item.PrefillWorker?.Name ?? "-"} " +
			$"decode_node={item.DecodeWorker?.Name ?? "-"} " +
			$"prefill_model={prefillModel} decode_model={decodeModel} " +
			$"prefill_ms={item.Phases.GetValueOrDefault("prefill_ms")} " +
			$"save_kv_ms={item.Phases.GetValueOrDefault("save_kv_ms")} " +
			$"restore_kv_ms={item.Phases.GetValueOrDefault("restore_kv_ms")} " +
			$"decode_ms={item.Phases.GetValueOrDefault("decode_ms")} " +
			$"total_ms={item.Phases.GetValueOrDefault("total_ms")} " +
			$"tokens_in={item.TokensIn} tokens_out={item.TokensOut} kv_bytes={item.KvBytes} " +
			$"prefix_hit={(item.PrefixCacheHit ? "true" : "false")} " +
			$"status=done"
		);
	}

	// ── Core KV save helpers (shared by SaveKvAsync + eviction sites) ──

	private async Task<byte[]?> SaveKvStateCoreAsync(
		WorkerConfig worker, int slotId, string sessionId, int nPast, string traceId, CancellationToken ct)
	{
		var llamaRpc = GetLlamaRpcClient(worker);
		var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
			slotId.ToString(), ReadOnlyMemory<byte>.Empty, traceId, ct);
		if (stateResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
			return null;

		// M-Perf.9 #289: capture model identity of the slot that built this KV
		// so the cross-model guard in RestoreKvAsync can detect a model swap
		// between prefill and decode. SlotMeta is enriched with model_alias +
		// model_hash + model_path (see META RPC 0x32). On an older binary that
		// doesn't carry the fields, all three are "" and the guard skips.
		string modelAlias = "", modelHash = "", modelPath = "";
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
					modelAlias = meta.ModelAlias ?? "";
					modelHash  = meta.ModelHash  ?? "";
					modelPath  = meta.ModelPath  ?? "";
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
				modelAlias, modelHash, modelPath);
			if (_chunkCache != null)
			{
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

	private async Task<int> PushMissingChunksAsync(string storeKey, string sessionId, List<string> missing, List<ChunkRef> allChunks, byte[] stateData, string traceId, CancellationToken ct)
	{
		if (missing.Count == 0) return 0;
		const int BatchBytes = 32 * 1024 * 1024;
		using var batch = new MemoryStream();
		int pushed = 0;
		async Task FlushAsync()
		{
			if (batch.Length == 0) return;
			await StoreClient.RequestAsync(OpCode.PushChunks, storeKey, batch.ToArray(), traceId, ct);
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
			pushed++;
			if (batch.Length >= BatchBytes) await FlushAsync();
		}
		await FlushAsync();
		return pushed;
	}

	private async Task PutManifestAsync(
		string storeKey, int nPast, long totalSize, List<ChunkRef> chunks,
		string traceId, CancellationToken ct,
		// M-Perf.9 #289: model identity of the slot that built this KV. The
		// RestoreKvAsync cross-model guard reads this back via GetManifestAsync
		// so it survives a Coordinator restart. Pre-#289 callers pass "" for
		// all three; the guard treats "both empty" as "skip".
		string modelAlias = "", string modelHash = "", string modelPath = "")
	{
		var manifest = new
		{
			n_past = nPast,
			total_size = totalSize,
			model_alias = modelAlias,
			model_hash  = modelHash,
			model_path  = modelPath,
			chunks = chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size }),
		};
		var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
		var resp = await StoreClient.RequestAsync(OpCode.PutManifest, storeKey, payload, traceId, ct);
		if (resp.Status != (byte)StatusCode.Ok)
			throw new InvalidDataException($"PUT_MANIFEST failed (status=0x{resp.Status:X2}): {resp.Meta}");
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
			foreach (var c in chunks)
			{
				var offset = c.Index * _cfg.ChunkSize;
				if (offset + c.Size <= prevState.Length)
				{
					var prevHash = ChunkEngine.ComputeHash(prevState.AsSpan(offset, c.Size));
					if (prevHash == c.Hash)
					{
						knownHashes.Add(c.Hash);
						Array.Copy(prevState, offset, blob, offset, c.Size);
					}
				}
			}
		}
		// Also check local chunk cache
		if (_chunkCache != null)
		{
			foreach (var c in chunks.Where(c => !knownHashes.Contains(c.Hash)))
			{
				var data = await _chunkCache.GetChunkDataAsync(storeKey, c.Hash, ct);
				if (data != null)
				{
					knownHashes.Add(c.Hash);
					var offset = c.Index * _cfg.ChunkSize;
					Array.Copy(data, 0, blob, offset, data.Length);
				}
			}
		}
		// Fetch remaining missing chunks from Store
		var missingHashes = chunks.Where(c => !knownHashes.Contains(c.Hash)).Select(c => c.Hash).ToList();
		if (missingHashes.Count > 0)
		{
			// Send only the hashes the client ALREADY has (from prevState + local cache).
			// The Store diffs them against the manifest and returns the missing chunks.
			// Sending all manifest hashes would cause the Store to return nothing,
			// leaving the assembled blob as all zeros.
			var knownList = JsonSerializer.SerializeToUtf8Bytes(knownHashes.ToList());
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
		IAsyncEnumerable<byte[]> source, WorkItem item)
	{
		string? lastUtf8 = null;
		await foreach (var chunk in source)
		{
			yield return chunk;
			// Skip [DONE] marker so lastUtf8 holds the actual usage/data chunk
			if (chunk.Length > 0)
			{
				var s = Encoding.UTF8.GetString(chunk).Trim();
				if (s != "data: [DONE]")
					lastUtf8 = s;
			}
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
						}
					}
				}
			}
			catch { }
		}
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

	private readonly Dictionary<string, LlamaClient> _llamaClients = new();

	private LlamaClient GetLlamaClient(WorkerConfig w)
	{
		if (_llamaClients.TryGetValue(w.Name, out var c)) return c;
		var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
		c = new LlamaClient(http, w.LlamaUrl);
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

	private Hydra.Shared.RpcClient GetLlamaRpcClient(WorkerConfig w)
	{
		if (_llamaRpcClients.TryGetValue(w.Name, out var c)) return c;
		var rpcHost = new Uri(w.LlamaUrl).Host;
		// Honor the injectable factory so tests never open real sockets.
		var client = AgentClientFactory != null
			? AgentClientFactory(rpcHost, w.LlamaRpcPort)
			: new Hydra.Shared.RpcClient(rpcHost, w.LlamaRpcPort);
		_llamaRpcClients[w.Name] = client;
		return client;
	}
}
