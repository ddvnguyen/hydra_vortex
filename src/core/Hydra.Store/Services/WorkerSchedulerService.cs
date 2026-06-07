using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hydra.Store.Models;
using Hydra.Store.Repositories;
using Serilog;

namespace Hydra.Store.Services;

public sealed class WorkerSchedulerService : IWorkerScheduler
{
	private readonly CoordinatorConfig _cfg;
	private readonly ISessionLedger _ledger;
	private readonly IWorkerTracker _tracker;
	private readonly ICompletionProxyService _proxy;
	private readonly IHealthMonitorService _health;
	private readonly Hydra.Shared.RpcClient? _storeClient;
	private readonly ILogger _log;
	private readonly Channel<WorkItem> _queue;
	private readonly CancellationTokenSource _cts = new();
	internal readonly Dictionary<string, Hydra.Shared.RpcClient> _agentClients = new();
	private readonly HashSet<string> _prefixSet = [];

	/// <summary>
	/// Injectable factory for creating agent RPC clients.
	/// Set in tests to return tracking test doubles.
	/// </summary>
	internal Func<string, int, Hydra.Shared.RpcClient>? AgentClientFactory { get; set; }
	private readonly ConcurrentDictionary<string, SlotLease> _warmLeases = new();

	private static readonly BoundedChannelOptions ChannelOpts = new(500)
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
		ILogger log)
	{
		_cfg = config; _ledger = ledger; _tracker = tracker; _proxy = proxy;
		_health = health; _storeClient = storeClient; _log = log;
		_queue = Channel.CreateBounded<WorkItem>(ChannelOpts);
		log.Information("Scheduler init: workers={Workers} mix={Mix}", string.Join(",", config.Workers.Select(w => w.Name)), config.MixPrecisionEnabled);
	}

	public async Task<object> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages,
		string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct)
	{
		var traceId = Router.NewTraceId();
		var item = new WorkItem(request, messages, sessionId, traceId, prefixHash, estimatedTokens, maxTokens);

		_log.Information("request_received Sid={Sid} Stream={Stream}",
			sessionId, item.IsStreaming);

		await _queue.Writer.WriteAsync(item, ct);

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
		try
		{
			return (await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1800), linked.Token))!;
		}
		catch (OperationCanceledException) { item.Cancel(); throw; }
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
		var client = GetAgent(targetWorker);
		var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked,
			$"{sessionId}:0", ReadOnlyMemory<byte>.Empty, traceId, ct);

		var nPastAfter = 0;
		if (resp.Meta != null)
		{
			var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
			nPastAfter = meta?.TryGetValue("n_past", out var n) == true
				? n.GetInt32() : 0;
		}

		_ledger.Register(sessionId, targetNodeName, 0, nPastAfter, entry.PrefixHash);
		_log.Information("migrate_done Sid={Sid} To={Node} NPast={N}",
			sessionId, targetNodeName, nPastAfter);

		return new { migrated = true, session_id = sessionId, target = targetNodeName, n_past = nPastAfter };
	}

	public async Task RunAsync(CancellationToken ct)
	{
		var reader = _queue.Reader;
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await reader.WaitToReadAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			while (reader.TryRead(out var item))
			{
				await ProcessAsync(item, ct);
			}
		}
	}

	internal async Task ProcessAsync(WorkItem item, CancellationToken ct)
	{
		if (item.IsCancelled)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
			return;
		}

		WorkItemState next;

		try
		{
			next = await DispatchAsync(item, ct);
		}
		catch (OperationCanceledException)
		{
			await FinalizeAsync(item, WorkItemState.Cancelled);
			return;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "handler_crashed State={State}", item.State);
			item.Error = ex;
			await FinalizeAsync(item, WorkItemState.Failed);
			return;
		}

		if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
		{
			await FinalizeAsync(item, next);
			return;
		}

		if (next == WorkItemState.None)
		{
			await Task.Delay(50, ct);
			await _queue.Writer.WriteAsync(item, ct);
			return;
		}

		var prev = item.State;
		item.State = next;
		_log.Information("state_transition Sid={Sid} {Prev}->{Next} ms={Ms}",
			item.SessionId, prev, next, item.ElapsedMs);
		await _queue.Writer.WriteAsync(item, ct);
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
					LeaseLifetime.Short, _tracker);
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
			if (dw != null && _tracker.Acquire(dw.Name, "decode"))
			{
				item.RouteType = "cold_atomic";
				item.DecodeWorker = dw;
				LastDispatchedNode = dw.Name;
				return WorkItemState.ModelLoadDecode;
			}
		}

		bool atomic = _cfg.RunMode == "fast"
			|| (!_cfg.MixPrecisionEnabled && item.EstimatedNewTokens <= _cfg.AtomicTokenThreshold);
		item.RouteType = atomic ? "cold_atomic" : "cold_concurrency";

		var pfWorker = Router.PickBestPrefillWorker(_cfg.Workers, _tracker, _health,
			item.EstimatedTokens);
		if (pfWorker != null && _tracker.Acquire(pfWorker.Name, "prefill"))
		{
			item.PrefillWorker = pfWorker;
			LastDispatchedNode = pfWorker.Name;
			return WorkItemState.ModelLoadPrefill;
		}

		return WorkItemState.None;
	}

	private async Task<WorkItemState> ModelLoadAsync(WorkItem item)
	{
		if (_cfg.MixPrecisionEnabled)
		{
			var w = item.State == WorkItemState.ModelLoadPrefill
				? item.PrefillWorker! : item.DecodeWorker!;
			var m = item.State == WorkItemState.ModelLoadPrefill
				? Router.PrefillModel(w) : Router.DecodeModel(w);
			if (m != null)
				_log.Information("model_load_skip Model={M} Worker={W}", m, w.Name);
		}
		return item.State == WorkItemState.ModelLoadPrefill
			? WorkItemState.PrefixRestore
			: WorkItemState.RestoreKv;
	}

	private async Task<WorkItemState> PrefixRestoreAsync(WorkItem item, CancellationToken ct)
	{
		if (!_cfg.PrefixCheckpointEnabled || item.PrefixHash == null
			|| item.PrefillWorker == null)
			return WorkItemState.Prefill;

		try
		{
			var prefixKey = $"{item.PrefillWorker.Name}:{item.PrefixHash}";
			var client = GetAgent(item.PrefillWorker);
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked,
				$"prefix/{item.PrefixHash}:0", ReadOnlyMemory<byte>.Empty,
				item.TraceId, ct);
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
		var client = GetAgent(item.PrefillWorker!);
		var key = $"{item.SessionId}:{item.PrefillSlot ?? 0}";
		try
		{
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.SaveStateChunked,
				key, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			if (_storeClient != null)
			{
				var metaJson = JsonSerializer.Serialize(new
				{
					session_id = item.SessionId,
					slot_id = item.PrefillSlot,
					n_past = item.NPastAfter
				});
				await _storeClient.RequestAsync(Hydra.Shared.OpCode.PutMeta,
					$"save:{item.SessionId}", Encoding.UTF8.GetBytes(metaJson),
					item.TraceId, ct);
			}

			// Ensure session ledger has HasStoreState = true
			var entry = _ledger.Register(item.SessionId, item.PrefillWorker!.Name, item.PrefillSlot, item.NPastAfter, item.PrefixHash);
			lock (entry) { entry.HasStoreState = true; }
			item.Entry = entry;

			_log.Information("state_saved Sid={Sid}", item.SessionId);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "save_failed");
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
				LeaseLifetime.Short, _tracker);
			LastDispatchedNode = dw.Name;
			return WorkItemState.ModelLoadDecode;
		}

		return WorkItemState.None;
	}

	private async Task<WorkItemState> RestoreKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		var client = GetAgent(w);
		try
		{
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked,
				$"{item.SessionId}:0", ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			if (resp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
				item.NPastAfter = meta?.TryGetValue("n_past", out var n) == true
					? n.GetInt32() : 0;
			}
			_log.Information("state_restored Sid={Sid} NPast={N}",
				item.SessionId, item.NPastAfter);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "restore_failed");
			return WorkItemState.Failed;
		}
		_ledger.Register(item.SessionId, w.Name, 0, item.NPastAfter, item.PrefixHash);
		item.Phases["restore_kv_ms"] = item.ElapsedMs;
		return WorkItemState.Decode;
	}

	// ── Gap 4: n_past tracking from decode ──
	private async Task<WorkItemState> DecodeAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		if (item.IsStreaming)
		{
			item.DecodeChunks = TrackStreamNPast(
				_proxy.ProxyCompletionStreamAsync(w.LlamaUrl, item.Request, item.TraceId, ct),
				item);
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
				await client.RequestAsync(Hydra.Shared.OpCode.SaveStateChunked,
					key, ReadOnlyMemory<byte>.Empty, item.TraceId,
					CancellationToken.None);
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

		// Decode lease: long-lived stays warm, short-lived disposed
		if (item.DecodeLease != null)
		{
			if (item.DecodeLease.Lifetime == LeaseLifetime.Long
				&& end == WorkItemState.Done)
			{
				_warmLeases[item.SessionId] = item.DecodeLease;
			}
			else
			{
				await item.DecodeLease.DisposeAsync();
			}

			item.DecodeLease = null;
		}

		item.Phases["total_ms"] = item.ElapsedMs;
		_log.Information("timeline Sid={Sid} Route={Route} End={End} Phases={Phases}",
			item.SessionId, item.RouteType, end, JsonSerializer.Serialize(item.Phases));
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
