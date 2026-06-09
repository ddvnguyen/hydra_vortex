using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
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
	private readonly IServiceProvider _serviceProvider;
	private readonly ILogger _log;
	private readonly Channel<WorkItem> _mainQueue;
	private readonly Channel<WorkItem> _prefillQueue;
	private readonly Channel<WorkItem> _decodeQueue;
	private readonly CancellationTokenSource _cts = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _agentClients = new();
	private readonly HashSet<string> _prefixSet = [];

	/// <summary>
	/// Injectable factory for creating agent RPC clients.
	/// Set in tests to return tracking test doubles.
	/// </summary>
	internal Func<string, int, Hydra.Shared.RpcClient>? AgentClientFactory { get; set; }
	private readonly ConcurrentDictionary<string, SlotLease> _warmLeases = new();
	private readonly ConcurrentDictionary<string, byte> _streamCompleted = new(); // sessions whose streaming has finished
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
		ILogger log)
	{
		_cfg = config; _ledger = ledger; _tracker = tracker; _proxy = proxy;
		_health = health; _storeClient = storeClient; _serviceProvider = serviceProvider; _log = log;
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

		_log.Information("request_received Sid={Sid} Stream={Stream}", sessionId, item.IsStreaming);

		await _mainQueue.Writer.WriteAsync(item, ct);

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
		// Streaming: return the chunk enumerable as soon as decode phase produces it
		if (item.IsStreaming)
		{
			try
			{
				return await item.StreamCompletion.Task.WaitAsync(TimeSpan.FromSeconds(600), linked.Token);
			}
			catch (OperationCanceledException)
			{
				item.Cancel();
				throw;
			}
		}
		else
		{
			// Non-streaming: wait for full response
			try
			{
				return (await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1800), linked.Token))!;
			}
			catch (OperationCanceledException)
			{
				item.Cancel();
				throw;
			}
		}
	}

	public async Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct)
	{
		var traceId = Router.NewTraceId();
		var entry = _ledger.Lookup(sessionId);
		if (entry == null || !entry.HasStoreState)
			throw new InvalidOperationException("Session not migratable");

		var targetWorker = _cfg.Workers.FirstOrDefault(w => w.Name == targetNodeName && w.CanDecode)
			?? throw new InvalidOperationException($"Target worker '{targetNodeName}' not found or cannot decode");

		// Restore KV state on target
		var migEntry = _ledger.Lookup(sessionId);
		var migSlot = migEntry?.SlotId ?? 0;
		var client = GetAgent(targetWorker);
		var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked,
			$"{sessionId}:{migSlot}", ReadOnlyMemory<byte>.Empty, traceId, ct);

		var nPastAfter = 0;
		if (resp.Meta != null)
		{
			var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
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

		var tasks = new[]
		{
			RunClassifierAsync( 1, ct),
			RunPrefillConsumerAsync(prefillslots, ct),
			RunDecodeConsumerAsync(decodeSlots, ct),
		};
		await Task.WhenAll(tasks);
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
		WorkItemState.SaveDone => MarkEvictedState(item),
		WorkItemState.MarkEvicted => MarkEvictedState(item),
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
			// n_tokens guard: if the new prompt has fewer tokens than the cached
			// n_past, llama.cpp will silent wipe the entire restored KV cache
			// (n_tokens MUST be > n_past). Evict the warm slot and force a cold route.
			if (entry.NPast > 0 && item.EstimatedTokens > 0
				&& item.EstimatedTokens < entry.NPast + 50)
			{
				_log.Warning("n_past_guard Evicted={Sid} Est={Est} NPast={Past} — warm slot would nuke cache",
					item.SessionId, item.EstimatedTokens, entry.NPast);
				// Release warm decode lease if holding one
				if (_warmLeases.TryRemove(item.SessionId, out var warmLease))
				{
					await warmLease.DisposeAsync();
					_decodeSlotSignal.Release();
				}
				_ledger.MarkEvicted(item.SessionId);
				item.State = WorkItemState.RouteDecision;
				return await ColdRouteAsync(item);
			}

			var target = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
			if (target != null && _tracker.TryAcquireSlot(target.Name, out var slot, "decode"))
			{
				item.RouteType = "affinity";
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
				if (entry.NPast > 0 && entry.NPast > _cfg.AtomicTokenThreshold * 4
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
			item.State = WorkItemState.PickDecode;
			return await PickDecodeAsync(item);
		}

		// Cold path — need prefill
		item.State = WorkItemState.RouteDecision;
		return await ColdRouteAsync(item);
	}

	private async Task<WorkItemState> ColdRouteAsync(WorkItem item)
	{
		// Small requests bypass the prefill-optimized worker → go direct to decode.
		// Overrides MixPrecisionEnabled — small prompts don't benefit from P/D split.
		if (item.EstimatedTokens <= _cfg.SmallRequestBypassThreshold)
		{
			var dw = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, _health);
			_log.Information("cold_atomic_try Est={Est} DecodeWorker={Dw} DecodeFree={Free} DecodeHealthy={Healthy}",
				item.EstimatedTokens, dw?.Name ?? "none",
				dw != null ? _tracker.IsFree(dw.Name) : false,
				dw != null ? _health.IsHealthy(dw.Name) : false);

			if (dw != null && _tracker.TryAcquireSlot(dw.Name, out var slot, "decode"))
			{
				item.RouteType = "cold_atomic";
				item.DecodeWorker = dw;
				item.DecodeSlot = slot;
				item.DecodeLease = new SlotLease(dw.Name, slot, item.SessionId, LeaseLifetime.Long, _tracker);
				LastDispatchedNode = dw.Name;
				return WorkItemState.ModelLoadDecode;
			}
		}

		bool atomic = _cfg.RunMode == "fast"
			|| (!_cfg.MixPrecisionEnabled && item.EstimatedNewTokens <= _cfg.AtomicTokenThreshold);
		item.RouteType = atomic ? "cold_atomic" : "cold_concurrency";

		var pfWorker = Router.PickBestPrefillWorker(_cfg.Workers, _tracker, _health, item.EstimatedTokens);
		// If no prefill worker has free slots, evict oldest warm lease to make room
		if (pfWorker == null && _warmLeases.Count > 0)
		{
			var oldest = _warmLeases.OrderBy(kv => kv.Value.CreatedAt).First();
			_log.Information("evicting_warm_slot Sid={Sid} Worker={W} Slot={Slot}",
				oldest.Key, oldest.Value.WorkerName, oldest.Value.SlotId);
			await oldest.Value.DisposeAsync();
			_warmLeases.TryRemove(oldest.Key, out _);
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
			var prefixKey = $"{item.PrefillWorker.Name}:{item.PrefixHash}";
			var client = GetAgent(item.PrefillWorker);
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked, $"prefix/{item.PrefixHash}:0", ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			_prefixSet.Add(prefixKey);

			// Gap 4: track n_past from prefix checkpoint restore
			if (resp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
				var nPast = meta?.TryGetValue("n_past", out var n) == true
					? n.GetInt32() : 0;
				if (nPast > 0)
					_ledger.UpdateNPast(item.SessionId, nPast);
			}

			_log.Information("prefix_restored Sid={Sid} Hash={Hash}",
				item.SessionId, item.PrefixHash);
		}
		catch (Exception ex) { _log.Warning(ex, "prefix_restore_failed"); }

		return WorkItemState.Prefill;
	}

	// ── Gap 4: n_past tracking in prefill ──
	private async Task<WorkItemState> PrefillAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var body = new Dictionary<string, object>(item.Request)
		{
			["stream"] = false,
			["max_tokens"] = 1
		};
		item.PrefillSlot = await Router.PickIdleSlot(w.LlamaUrl, ct) ?? 0;
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

		item.Phases["prefill_ms"] = item.ElapsedMs;
		return WorkItemState.SaveKv;
	}

	private async Task<WorkItemState> SaveKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var slotId = item.PrefillSlot ?? 0;
		var key = $"kv/{item.SessionId}";
		_log.Information("save_kv_start Sid={Sid} Key={Key} Slot={Slot} NPast={N} Node={Node}",
			item.SessionId, key, slotId, item.NPastAfter, w.Name);
		try
		{
			var llama = GetLlamaClient(w);
			using var stateResult = await llama.GetStateAsync(slotId, ct);
			var buf = new byte[stateResult.ContentLength];
			var offset = 0;
			while (offset < buf.Length)
			{
				var n = await stateResult.Content.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct);
				if (n == 0) break;
				offset += n;
			}
			stateResult.Dispose();

			// Write to in-process Store (/mnt/llm-ram)
			var storeDir = new DirectoryInfo(Environment.GetEnvironmentVariable("HYDRA_STORE_DIR") ?? "/mnt/llm-ram/store");
			storeDir.Create();
			var path = Path.Combine(storeDir.FullName, $"{item.SessionId}.kv");
			await File.WriteAllBytesAsync(path, buf, ct);

			var entry = _ledger.Register(item.SessionId, w.Name, slotId, item.NPastAfter, item.PrefixHash);
			lock (entry) { entry.HasStoreState = true; }
			item.Entry = entry;
			_log.Information("state_saved Sid={Sid} SizeMB={Size}", item.SessionId, buf.Length / 1024 / 1024);
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
				item.Phases["save_kv_ms"] = item.ElapsedMs;
				return WorkItemState.Decode;
			}
			_log.Error("save_fallback_no_slot Sid={Sid} — prefill node has no free decode slot", item.SessionId);
			return WorkItemState.Failed;
		}
		item.Phases["save_kv_ms"] = item.ElapsedMs;
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

	private WorkItemState MarkEvictedState(WorkItem item)
	{
		_ = MarkEvictedStateAsync(item);
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
		var slotId = Math.Min(item.PrefillSlot ?? entry?.SlotId ?? 0, w.Slots - 1);
		var restoreKey = $"{item.SessionId}:{slotId}";
		_log.Information("restore_kv_start Sid={Sid} Key={Key} Node={Node} Slot={Slot}",
			item.SessionId, restoreKey, w.Name, slotId);
		try
		{
			// Read KV state from in-process Store
			var storeDir = new DirectoryInfo(Environment.GetEnvironmentVariable("HYDRA_STORE_DIR") ?? "/mnt/llm-ram/store");
			var path = Path.Combine(storeDir.FullName, $"{item.SessionId}.kv");
			if (!File.Exists(path))
				throw new FileNotFoundException($"No saved KV state for {item.SessionId}", path);
			var buf = await File.ReadAllBytesAsync(path, ct);

			// Push to target llama-server via HTTP
			var llama = GetLlamaClient(w);
			using var ms = new MemoryStream(buf);
			var result = await llama.PutStateAsync(slotId, ms, buf.Length, ct);
			item.NPastAfter = result?.NPast ?? item.NPastAfter;
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
				item.Phases["restore_kv_ms"] = item.ElapsedMs;
				return WorkItemState.Decode;
			}
			_log.Warning(ex, "restore_skipped Sid={Sid} — continuing without KV restore", item.SessionId);
			item.NPastAfter = 0;
		}
		// Preserve existing slot + n_past if restore found nothing new
		if (item.NPastAfter > 0)
			_ledger.UpdateNPast(item.SessionId, item.NPastAfter);
		var existingSlot = entry?.SlotId ?? item.PrefillSlot ?? 0;
		_ledger.Register(item.SessionId, w.Name, existingSlot, item.NPastAfter > 0 ? item.NPastAfter : entry?.NPast ?? 0, item.PrefixHash);
		item.Phases["restore_kv_ms"] = item.ElapsedMs;
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
		// Dump max_tokens and model from the actual request being sent
		var mt = item.Request.TryGetValue("max_tokens", out var mtv) ? mtv?.ToString() : "?";
		// Dump FULL request body being sent to llama-server for decode
		Console.Error.WriteLine($"event=decode_body Sid={item.SessionId} " + 
			System.Text.Json.JsonSerializer.Serialize(item.Request));
		_log.Information("decode_start Sid={Sid} Node={Node} Msgs={Msgs} LastMsg={Last} Streaming={Stream} NPast={N} MaxTokens={Mt}",
			item.SessionId, w.Name, msgCount, lastMsg, item.IsStreaming, item.NPastAfter, mt);
		if (item.IsStreaming)
		{
			var streamTask = _proxy.ProxyCompletionStreamAsync(w.LlamaUrl, item.Request, item.TraceId, ct);
			item.DecodeChunks = TrackStreamNPast(streamTask, item);
			item.StreamCompletion.TrySetResult(item.DecodeChunks);
			item.Response = new { streamed = true };
		}
		else
		{
			var resp = await _proxy.ProxyCompletionAsync(
				w.LlamaUrl, item.Request, item.TraceId, ct);
			if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se)
				item.LastIdSlot = se.GetInt32();
			item.Response = resp;

			// Track n_past from completion response
			TrackAfterCompletion(item.SessionId, resp);
		}
		item.Phases["decode_ms"] = item.ElapsedMs;
		return WorkItemState.BgSave;
	}

	private async Task<WorkItemState> BgSaveAsync(WorkItem item)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				var client = GetAgent(item.DecodeWorker!);
				var key = $"{item.SessionId}:{item.LastIdSlot ?? 0}";
				var opcode = _cfg.RawSlot ? Hydra.Shared.OpCode.SaveState : Hydra.Shared.OpCode.SaveStateChunked;
				await client.RequestAsync(opcode, key, ReadOnlyMemory<byte>.Empty, item.TraceId, CancellationToken.None);
				_log.Information("bg_saved Sid={Sid}", item.SessionId);
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
			// Erase slot via agent RPC
			var client = GetAgentByName(nodeName);
			if (client != null)
			{
				await client.RequestAsync(Hydra.Shared.OpCode.SlotErase,
					lease.SlotId.ToString(), ReadOnlyMemory<byte>.Empty,
					$"evict_{sessionId}", ct);
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
		_streamCompleted.TryAdd(sessionId, 0);
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

	private async Task FinalizeAsync(WorkItem item, WorkItemState end)
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
		if (item.DecodeLease != null)
		{
			if (item.DecodeLease.Lifetime == LeaseLifetime.Long
				&& end == WorkItemState.Done)
			{
				// If streaming already completed (short response), release immediately.
				// Otherwise store as warm — NotifyStreamComplete will release it.
				if (_streamCompleted.TryRemove(item.SessionId, out _))
				{
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

		item.Phases["total_ms"] = item.ElapsedMs;
		var node = item.PrefillWorker?.Name ?? item.DecodeWorker?.Name ?? "unknown";
		Console.Error.WriteLine(
			$"event=request_timeline trace_id={item.TraceId} session_id={item.SessionId} " +
			$"queue_wait_ms={item.Phases.GetValueOrDefault("queue_wait_ms")} node={node} " +
			$"route_type={item.RouteType} " +
			$"prefill_ms={item.Phases.GetValueOrDefault("prefill_ms")} " +
			$"save_kv_ms={item.Phases.GetValueOrDefault("save_kv_ms")} " +
			$"restore_kv_ms={item.Phases.GetValueOrDefault("restore_kv_ms")} " +
			$"decode_ms={item.Phases.GetValueOrDefault("decode_ms")} " +
			$"total_ms={item.Phases.GetValueOrDefault("total_ms")}"
		);
		if (item.Completion.Task.IsCompleted) return;
		if (end == WorkItemState.Done)
			item.Completion.TrySetResult(item.Response);
		else if (end == WorkItemState.Cancelled)
			item.Completion.TrySetCanceled();
		else
			item.Completion.TrySetException(
				item.Error ?? new InvalidOperationException("Failed"));
	}

	// ── Gap 4 helpers: n_past tracking ──

	private static int ExtractTotalTokens(Dictionary<string, object> result)
	{
		if (!result.TryGetValue("usage", out var u) || u is not JsonElement ue)
			return 0;
		if (!ue.TryGetProperty("total_tokens", out var tt))
			return 0;
		return tt.GetInt32();
	}

	private void TrackAfterCompletion(string sessionId, Dictionary<string, object> result)
	{
		var total = ExtractTotalTokens(result);
		if (total > 0)
		{
			_ledger.UpdateNPast(sessionId, total);
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
			if (chunk.Length > 0)
				lastUtf8 = Encoding.UTF8.GetString(chunk);
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
					if (data != null && data.TryGetValue("usage", out var u))
					{
						var usageDict = new Dictionary<string, object>
						{
							["usage"] = u
						};
						TrackAfterStream(item.SessionId, usageDict);
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
}
