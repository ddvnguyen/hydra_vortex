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
		_log.Information("migrate_done Sid={Sid} To={Node} NPast={N}", sessionId, targetNodeName, nPastAfter);

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
			var guardBaseline = entry.NPromptTokens > 0 ? entry.NPromptTokens : entry.NPast;
			if (guardBaseline > 0 && item.EstimatedTokens > 0
				&& item.EstimatedTokens < guardBaseline + 50)
			{
				_log.Warning("n_past_guard Evicted={Sid} Est={Est} GuardBaseline={Past} NPrompt={NP} NPast={Total} — warm slot would nuke cache",
					item.SessionId, item.EstimatedTokens, guardBaseline, entry.NPromptTokens, entry.NPast);
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
			await warmLease.DisposeAsync();
			_decodeSlotSignal.Release();
		}
		_ledger.MarkEvicted(item.SessionId);
		item.State = WorkItemState.RouteDecision;
		return await ColdRouteAsync(item);
	}

	private async Task<WorkItemState> ColdRouteAsync(WorkItem item)
	{
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
				CoordinatorMetrics.ColdSessionStarts.Inc();
				item.DecodeWorker = aw;
				item.DecodeSlot = slot;
				item.DecodeLease = new SlotLease(aw.Name, slot, item.SessionId, LeaseLifetime.Long, _tracker);
				LastDispatchedNode = aw.Name;
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
			CoordinatorMetrics.ColdSessionStarts.Inc();
			item.PrefillWorker = pfWorker;
			item.PrefillSlot = pfSlot;
			item.PrefillLease = new SlotLease(pfWorker.Name, pfSlot, item.SessionId,
				LeaseLifetime.Short, _tracker);
			LastDispatchedNode = pfWorker.Name;
			return WorkItemState.ModelLoadPrefill;
		}

		_log.Warning("cold_route_no_worker Est={Est} Workers={Workers}", item.EstimatedTokens, string.Join(",", _cfg.Workers.Select(w => $"{w.Name}(cd={w.CanDecode},cp={w.CanPrefill})")));
		return WorkItemState.None;
	}

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
		var body = new Dictionary<string, object>(item.Request)
		{
			["stream"] = false,
			["n_predict"] = 0
		};
		// Pin to the same slot where the prefix checkpoint was restored
		// (set by ColdRouteAsync, or by PrefixRestoreAsync). When the
		// prefix KV was loaded via StatePut, the slot already has n_past
		// cached tokens — using any other slot would waste them.
		if (item.PrefillSlot == null)
			item.PrefillSlot = await Router.PickIdleSlot(w.LlamaUrl, ct) ?? 0;
		body["id_slot"] = item.PrefillSlot.Value;
		var resp = await _proxy.ProxyCompletionAsync(w.LlamaUrl, body, item.TraceId, ct);
		if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se)
			item.PrefillSlot = se.GetInt32();
		item.LastIdSlot = item.PrefillSlot;

		// Extract n_past from prefill usage
		item.NPastAfter = ExtractTotalTokens(resp);
		_log.Information("prefill_done Sid={Sid} Node={Node} Slot={Slot} NPastFromLLama={N} EstTokens={Est}",
			item.SessionId, w.Name, item.PrefillSlot, item.NPastAfter, item.EstimatedTokens);
		if (item.NPastAfter > 0)
		{
			_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
			// Resolve slot from health if slot_id is null
			if (item.PrefillSlot == null || item.PrefillSlot == 0)
				ResolveSlotFromHealth(item.SessionId, item.NPastAfter);
		}

		CoordinatorMetrics.PrefillDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("prefill_ms") / 1000.0);
		return WorkItemState.SaveKv;
	}

	private async Task<WorkItemState> SaveKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var slotId = item.PrefillSlot ?? 0;
		var storeKey = $"{item.SessionId}.kv";
		_log.Information("save_kv_start Sid={Sid} Key={Key} Slot={Slot} NPast={N} Node={Node}",
			item.SessionId, storeKey, slotId, item.NPastAfter, w.Name);
		try
		{
			var llamaRpc = GetLlamaRpcClient(w);
			var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
				slotId.ToString(), ReadOnlyMemory<byte>.Empty, item.TraceId, ct);

			if (stateResp.Status != (byte)Hydra.Shared.StatusCode.Ok)
				throw new InvalidOperationException($"StateGet RPC failed: status={stateResp.Status} meta={stateResp.Meta}");

			if (_cfg.EnableChunks)
			{
				// Chunked save: chunk → dedup → push only missing → write manifest
				var chunks = ChunkEngine.ChunkAndHash(stateResp.Payload);
				var orderedHashes = chunks.Select(c => c.Hash).ToList();
				var missing = await SyncMissingAsync(storeKey, orderedHashes, item.TraceId, ct);
				await PushMissingChunksAsync(storeKey, item.SessionId, missing, chunks, stateResp.Payload, item.TraceId, ct);
				await PutManifestAsync(storeKey, item.NPastAfter, stateResp.Payload.Length, chunks, item.TraceId, ct);
				var deduped = chunks.Count - missing.Count;
				_log.Information("state_saved_chunked Sid={Sid} SizeMB={Size} Chunks={Total} New={New} Deduped={Dup}",
					item.SessionId, stateResp.Payload.Length / 1024 / 1024, chunks.Count, missing.Count, deduped);
				if (_chunkCache != null)
				{
					await _chunkCache.SaveHashesAsync(item.SessionId, orderedHashes, ct);
					foreach (var c in chunks)
						await _chunkCache.SaveChunkDataAsync(item.SessionId, c.Hash,
							stateResp.Payload.AsSpan(c.Index * _cfg.ChunkSize, Math.Min(_cfg.ChunkSize, (int)(stateResp.Payload.Length - c.Index * _cfg.ChunkSize))).ToArray(), ct);
				}
			}
			else
			{
				await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
					storeKey, stateResp.Payload, item.TraceId, ct);
			}

			item.KvBytes = stateResp.Payload.Length;
			var entry = _ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter, item.PrefixHash);
			lock (entry) { entry.HasStoreState = true; }
			item.Entry = entry;
			_log.Information("state_saved Sid={Sid} SizeMB={Size}", item.SessionId, stateResp.Payload.Length / 1024 / 1024);

			if (item.PrefixHash != null && _cfg.PrefixCheckpointEnabled)
			{
				var prefixKey = $"prefix/{item.PrefixHash}.kv";
				var kvPayload = stateResp.Payload;
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
			if (item.PrefillWorker?.Name == dw.Name
				&& (!_cfg.MixPrecisionEnabled
					|| Router.DecodeModel(dw) == null
					|| Router.DecodeModel(dw) == Router.PrefillModel(item.PrefillWorker!)))
			{
				_log.Information("same_node_skip Sid={Sid} Node={Node} — KV already resident",
					item.SessionId, dw.Name);
				return WorkItemState.Decode;
			}

			return WorkItemState.ModelLoadDecode;
		}

		// No free decode slots — evict oldest warm lease and retry
		if (_warmLeases.Count > 0)
		{
			var oldest = _warmLeases.OrderBy(kv => kv.Value.CreatedAt).First();
			_log.Information("evicting_warm_decode Sid={Sid} Worker={W} Slot={Slot}",
				oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId);
			await oldest.Value.DisposeAsync();
			_warmLeases.TryRemove(oldest.Key, out _);
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
			_log.Information("state_restored Sid={Sid} NPast={N} Node={Node}",
				item.SessionId, item.NPastAfter, w.Name);
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
			// Ask llama-server to emit a final usage chunk so token counts are
			// available on streamed requests (OpenAI omits usage from streams by default).
			item.Request["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
			var cts = CancellationTokenSource.CreateLinkedTokenSource(item.HttpCancellationToken, ct);
			_pipelineCts[item.SessionId] = cts;
			var streamTask = _proxy.ProxyCompletionStreamAsync(w.LlamaUrl, item.Request, item.TraceId, cts.Token);
			item.DecodeChunks = TrackStreamNPast(streamTask, item);
			// Defer BgSave until stream completes — slot is still processing now.
			// Set before StreamCompletion to avoid race: a fast stream could finish
			// and call NotifyStreamComplete before this line runs, orphaning the save.
			_pendingBgSaves[item.SessionId] = (w.Name, item.DecodeSlot ?? 0, item.TraceId);
			item.StreamCompletion.TrySetResult(item.DecodeChunks);
			item.Response = new { streamed = true };
			return WorkItemState.Done;
		}
		else
		{
			using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(item.HttpCancellationToken, ct);
			var resp = await _proxy.ProxyCompletionAsync(
				w.LlamaUrl, item.Request, item.TraceId, syncCts.Token);
			if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se)
				item.LastIdSlot = se.GetInt32();
			item.Response = resp;
			item.TokensIn = ExtractUsageInt(resp, "prompt_tokens");
			item.TokensOut = ExtractUsageInt(resp, "completion_tokens");

			// Track n_past from completion response
			TrackAfterCompletion(item.SessionId, resp);
		}
		CoordinatorMetrics.DecodeDuration.WithLabels(w.Name, RouteLabel(item))
			.Observe(item.RecordPhase("decode_ms") / 1000.0);
		return WorkItemState.BgSave;
	}

	private async Task<WorkItemState> BgSaveAsync(WorkItem item)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				var w = item.DecodeWorker!;
				var slotId = item.LastIdSlot ?? 0;
				var storeKey = $"{item.SessionId}.kv";
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
			catch (Exception ex) { _log.Error(ex, "bg_save_failed"); }
		});
		return WorkItemState.Done;
	}

	// ── Warm lease eviction ──

	public async Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct)
	{
		if (!_warmLeases.TryRemove(sessionId, out var lease))
			return;

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

	public void NotifyStreamComplete(string sessionId)
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

		// Fire deferred BgSave — the slot is now idle, StateGet will succeed
		if (_pendingBgSaves.TryRemove(sessionId, out var bgInfo2))
		{
			var w = _cfg.Workers.FirstOrDefault(x => x.Name == bgInfo2.WorkerName);
			if (w != null)
			{
				_ = Task.Run(async () =>
				{
					try
					{
						var llamaRpc = GetLlamaRpcClient(w);
						var stateResp = await llamaRpc.RequestAsync(Hydra.Shared.OpCode.StateGet,
							bgInfo2.SlotId.ToString(), ReadOnlyMemory<byte>.Empty, bgInfo2.TraceId, CancellationToken.None);
						if (stateResp.Status == (byte)Hydra.Shared.StatusCode.Ok)
						{
							await StoreClient.RequestAsync(Hydra.Shared.OpCode.Put,
								$"{sessionId}.kv", stateResp.Payload, bgInfo2.TraceId, CancellationToken.None);
							_ledger.MarkStoreState(sessionId);
							_log.Information("bg_saved Sid={Sid}", sessionId);
						}
						else
						{
							_log.Warning("bg_save_busy Sid={Sid} Status={Status}", sessionId, stateResp.Status);
						}
					}
					catch (Exception ex) { _log.Error(ex, "bg_save_failed"); }
				});
			}
		}

		if (_warmLeases.TryRemove(sessionId, out var lease))
		{
			_log.Information("stream_done_release Sid={Sid} Worker={W} Slot={Slot}",
				sessionId, lease.WorkerName, lease.SlotId);
			lease.DisposeAsync().AsTask().ContinueWith(_ => { });
			_decodeSlotSignal.Release();
		}
		else
		{
			_log.Warning("stream_done_no_lease Sid={Sid} WarmKeys={Keys}",
				sessionId, string.Join(",", _warmLeases.Keys.Take(5)));
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

	private async Task PutManifestAsync(string storeKey, int nPast, long totalSize, List<ChunkRef> chunks, string traceId, CancellationToken ct)
	{
		var manifest = new
		{
			n_past = nPast,
			total_size = totalSize,
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
			var knownList = JsonSerializer.SerializeToUtf8Bytes(chunks.Select(c => c.Hash).ToList());
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
