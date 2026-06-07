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
	private readonly Hydra.Shared.RpcClient? _storeClient;
	private readonly ILogger _log;
	private readonly Channel<WorkItem> _queue;
	private readonly CancellationTokenSource _cts = new();
	private readonly Dictionary<string, Hydra.Shared.RpcClient> _agentClients = new();
	private readonly HashSet<string> _prefixSet = [];

	private static readonly BoundedChannelOptions ChannelOpts = new(500)
	{
		FullMode = BoundedChannelFullMode.Wait,
		SingleWriter = false,
		SingleReader = true
	};

	public string? LastDispatchedNode { get; private set; }

	public WorkerSchedulerService(
		CoordinatorConfig config, ISessionLedger ledger, IWorkerTracker tracker,
		ICompletionProxyService proxy, Hydra.Shared.RpcClient? storeClient, ILogger log)
	{
		_cfg = config; _ledger = ledger; _tracker = tracker; _proxy = proxy;
		_storeClient = storeClient; _log = log;
		_queue = Channel.CreateBounded<WorkItem>(ChannelOpts);
		log.Information("Scheduler init: workers={Workers} mix={Mix}",
			string.Join(",", config.Workers.Select(w => w.Name)), config.MixPrecisionEnabled);
	}

	public async Task<object> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages,
		string sessionId, int maxTokens, string? prefixHash, CancellationToken ct)
	{
		var traceId = Router.NewTraceId();
		// TODO this is bad for performance
		var estimated = Router.EstimateRequestTokens(messages, _cfg.CharsPerToken);
		var item = new WorkItem(request, messages, sessionId, traceId, prefixHash, estimated, maxTokens);

		_log.Information("request_received Sid={Sid} Est={Est} Stream={Stream}",
			sessionId, estimated, item.IsStreaming);

		await _queue.Writer.WriteAsync(item, ct);

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
		try { return (await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1800), linked.Token))!; }
		catch (OperationCanceledException) { item.Cancel(); throw; }
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

	private async Task ProcessAsync(WorkItem item, CancellationToken ct)
	{
		if (item.IsCancelled)
		{
			Finalize(item, WorkItemState.Cancelled); return;
		}

		WorkItemState next;

		try
		{
			next = await DispatchAsync(item, ct);
		}
		catch (OperationCanceledException)
		{
			Finalize(item, WorkItemState.Cancelled); return;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "handler_crashed State={State}", item.State);
			item.Error = ex;
			Finalize(item, WorkItemState.Failed);
			return;
		}

		if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
		{
			Finalize(item, next); return;
		}
		var prev = item.State; item.State = next;
		_log.Information("state_transition Sid={Sid} {Prev}->{Next} ms={Ms}", item.SessionId, prev, next, item.ElapsedMs);
		await _queue.Writer.WriteAsync(item, ct);
	}

	private async Task<WorkItemState> DispatchAsync(WorkItem item, CancellationToken ct) => item.State switch
	{
		WorkItemState.Pending or WorkItemState.RouteDecision => await RouteAsync(item),
		WorkItemState.WaitingPrefill => await WaitWorkerAsync(item, true),
		WorkItemState.WaitingDecode => await WaitWorkerAsync(item, false),
		WorkItemState.ModelLoadPrefill or WorkItemState.ModelLoadDecode => await ModelLoadAsync(item),
		WorkItemState.PrefixRestore => await PrefixRestoreAsync(item, ct),
		WorkItemState.Prefill => await PrefillAsync(item, ct),
		WorkItemState.SaveKv => await SaveKvAsync(item, ct),
		WorkItemState.SaveDone => MarkEvictedState(item),
		WorkItemState.MarkEvicted => MarkEvictedState(item),
		WorkItemState.PickDecode => PickDecode(item),
		WorkItemState.RestoreKv => await RestoreKvAsync(item, ct),
		WorkItemState.Decode => await DecodeAsync(item, ct),
		WorkItemState.BgSave => await BgSaveAsync(item),
		_ => WorkItemState.Failed
	};

	// ── Route ──
	private async Task<WorkItemState> RouteAsync(WorkItem item)
	{
		var entry = _ledger.Lookup(item.SessionId); item.Entry = entry;
		// Warm affinity
		if (entry != null && entry.SlotId.HasValue && !entry.SlotFreed)
		{
			var target = _cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
			if (target != null && _tracker.Acquire(target.Name, "decode"))
			{
				item.RouteType = "affinity"; item.DecodeWorker = target; item.DecodeSlot = entry.SlotId; LastDispatchedNode = target.Name;
				if (entry.NPast > 0 && entry.NPast > _cfg.AtomicTokenThreshold * 4 && item.EstimatedTokens < entry.NPast * 0.85)
				{   // N-past guard: estimated tokens too small, force restore
					_log.Warning("n_past_guard Sid={Sid} NPast={N} Est={E}", item.SessionId, entry.NPast, item.EstimatedTokens);
					_ledger.MarkEvicted(item.SessionId); _tracker.Release(target.Name); return WorkItemState.PickDecode;
				}
				_ledger.UpdateLastUsed(item.SessionId);
				return _cfg.MixPrecisionEnabled && Router.DecodeModel(target) != null ? WorkItemState.ModelLoadDecode : WorkItemState.Decode;
			}
		}
		if (entry != null && entry.HasStoreState) { item.RouteType = "migration"; return WorkItemState.PickDecode; }
		bool atomic = _cfg.RunMode == "fast" || (!_cfg.MixPrecisionEnabled && item.EstimatedNewTokens <= _cfg.AtomicTokenThreshold);
		item.RouteType = atomic ? "cold_atomic" : "cold_concurrency";
		return WorkItemState.WaitingPrefill;
	}

	// ── Wait Worker ──
	private async Task<WorkItemState> WaitWorkerAsync(WorkItem item, bool prefill)
	{
		var worker = prefill
			? Router.PickBestPrefillWorker(_cfg.Workers, _tracker, item.EstimatedTokens)
			: Router.PickBestDecodeWorker(_cfg.Workers, _tracker, item.PrefillWorker?.Name);
		if (worker != null && _tracker.Acquire(worker.Name, prefill ? "prefill" : "decode"))
		{
			if (prefill) item.PrefillWorker = worker; else item.DecodeWorker = worker;
			LastDispatchedNode = worker.Name;
			return prefill ? WorkItemState.ModelLoadPrefill : WorkItemState.ModelLoadDecode;
		}
		await Task.Delay(100); return item.State;
	}

	private async Task<WorkItemState> ModelLoadAsync(WorkItem item)
	{
		if (_cfg.MixPrecisionEnabled)
		{
			var w = item.State == WorkItemState.ModelLoadPrefill ? item.PrefillWorker! : item.DecodeWorker!;
			var m = item.State == WorkItemState.ModelLoadPrefill ? Router.PrefillModel(w) : Router.DecodeModel(w);
			if (m != null) _log.Information("model_load_skip Model={M} Worker={W}", m, w.Name);
		}
		return item.State == WorkItemState.ModelLoadPrefill ? WorkItemState.PrefixRestore : WorkItemState.RestoreKv;
	}

	private async Task<WorkItemState> PrefixRestoreAsync(WorkItem item, CancellationToken ct)
	{
		if (!_cfg.PrefixCheckpointEnabled || item.PrefixHash == null || item.PrefillWorker == null) return WorkItemState.Prefill;
		try
		{
			var client = GetAgent(item.PrefillWorker);
			await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked, $"prefix/{item.PrefixHash}:0", ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			_prefixSet.Add(item.PrefixHash);
			_log.Information("prefix_restored Sid={Sid} Hash={Hash}", item.SessionId, item.PrefixHash);
		}
		catch (Exception ex) { _log.Warning(ex, "prefix_restore_failed"); }
		return WorkItemState.Prefill;
	}

	private async Task<WorkItemState> PrefillAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.PrefillWorker!;
		var body = new Dictionary<string, object>(item.Request) { ["stream"] = false, ["max_tokens"] = 1 };
		item.PrefillSlot = await Router.PickIdleSlot(w.LlamaUrl, ct) ?? 0;
		var resp = await _proxy.ProxyCompletionAsync(w.LlamaUrl, body, item.TraceId, ct);
		if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se) item.PrefillSlot = se.GetInt32();
		item.LastIdSlot = item.PrefillSlot;
		item.Phases["prefill_ms"] = item.ElapsedMs;
		return WorkItemState.SaveKv;
	}

	private async Task<WorkItemState> SaveKvAsync(WorkItem item, CancellationToken ct)
	{
		var client = GetAgent(item.PrefillWorker!);
		var key = $"{item.SessionId}:{item.PrefillSlot ?? 0}";
		try
		{
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.SaveStateChunked, key, ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			if (_storeClient != null)
			{
				var metaJson = JsonSerializer.Serialize(new { session_id = item.SessionId, slot_id = item.PrefillSlot, n_past = item.NPastAfter });
				await _storeClient.RequestAsync(Hydra.Shared.OpCode.PutMeta, $"save:{item.SessionId}", Encoding.UTF8.GetBytes(metaJson), item.TraceId, ct);
			}
			if (item.Entry != null) item.Entry.HasStoreState = true;
			_log.Information("state_saved Sid={Sid}", item.SessionId);
		}
		catch (Exception ex) { _log.Error(ex, "save_failed"); return WorkItemState.Failed; }
		item.Phases["save_kv_ms"] = item.ElapsedMs;
		return WorkItemState.SaveDone;
	}

	private WorkItemState MarkEvictedState(WorkItem item)
	{
		_ledger.MarkEvicted(item.SessionId);
		if (item.PrefillWorker != null) _tracker.Release(item.PrefillWorker.Name);
		return item.State == WorkItemState.SaveDone ? WorkItemState.PickDecode : WorkItemState.Done;
	}

	private WorkItemState PickDecode(WorkItem item)
	{
		var dw = Router.PickBestDecodeWorker(_cfg.Workers, _tracker, item.PrefillWorker?.Name)
				 ?? (item.PrefillWorker?.CanDecode == true ? item.PrefillWorker : null);
		if (dw == null) { item.State = WorkItemState.WaitingDecode; return WorkItemState.WaitingDecode; }
		item.DecodeWorker = dw; LastDispatchedNode = dw.Name;
		return WorkItemState.WaitingDecode;
	}

	private async Task<WorkItemState> RestoreKvAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		var client = GetAgent(w);
		try
		{
			var resp = await client.RequestAsync(Hydra.Shared.OpCode.RestoreStateChunked, $"{item.SessionId}:0", ReadOnlyMemory<byte>.Empty, item.TraceId, ct);
			if (resp.Meta != null)
			{
				var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
				item.NPastAfter = meta?.TryGetValue("n_past", out var n) == true ? n.GetInt32() : 0;
			}
			_log.Information("state_restored Sid={Sid} NPast={N}", item.SessionId, item.NPastAfter);
		}
		catch (Exception ex) { _log.Error(ex, "restore_failed"); return WorkItemState.Failed; }
		_ledger.Register(item.SessionId, w.Name, 0, item.NPastAfter, item.PrefixHash);
		item.Phases["restore_kv_ms"] = item.ElapsedMs;
		return WorkItemState.Decode;
	}

	private async Task<WorkItemState> DecodeAsync(WorkItem item, CancellationToken ct)
	{
		var w = item.DecodeWorker!;
		if (item.IsStreaming)
		{
			item.DecodeChunks = _proxy.ProxyCompletionStreamAsync(w.LlamaUrl, item.Request, item.TraceId, ct);
		}
		else
		{
			var resp = await _proxy.ProxyCompletionAsync(w.LlamaUrl, item.Request, item.TraceId, ct);
			if (resp.TryGetValue("id_slot", out var s) && s is JsonElement se) item.LastIdSlot = se.GetInt32();
			item.Response = resp;
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
				await client.RequestAsync(Hydra.Shared.OpCode.SaveStateChunked, key, ReadOnlyMemory<byte>.Empty, item.TraceId, CancellationToken.None);
				_log.Information("bg_saved Sid={Sid}", item.SessionId);
			}
			catch (Exception ex) { _log.Error(ex, "bg_save_failed"); }
		});
		return WorkItemState.Done;
	}

	private void Finalize(WorkItem item, WorkItemState end)
	{
		item.State = end;
		if (item.PrefillWorker != null) _tracker.Release(item.PrefillWorker.Name);
		if (item.DecodeWorker != null && end != WorkItemState.Decode) _tracker.Release(item.DecodeWorker.Name);
		item.Phases["total_ms"] = item.ElapsedMs;
		_log.Information("timeline Sid={Sid} Route={Route} End={End} Phases={Phases}", item.SessionId, item.RouteType, end, JsonSerializer.Serialize(item.Phases));
		if (item.Completion.Task.IsCompleted) return;
		if (end == WorkItemState.Done) item.Completion.TrySetResult(item.Response);
		else if (end == WorkItemState.Cancelled) item.Completion.TrySetCanceled();
		else item.Completion.TrySetException(item.Error ?? new InvalidOperationException("Failed"));
	}

	private Hydra.Shared.RpcClient GetAgent(WorkerConfig w)
	{
		if (_agentClients.TryGetValue(w.Name, out var c)) return c;
		return _agentClients[w.Name] = new Hydra.Shared.RpcClient(w.Host, w.RpcPort);
	}
}
