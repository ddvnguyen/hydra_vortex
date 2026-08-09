using System.Diagnostics;
using System.Text.Json;
using Hydra.Core.Models;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>Runtime envelope for one submitted request, as it moves through the
/// queue and the state machine. Carries the typed request, the shared work item,
/// the routing decision (set at dispatch) and queue metadata.</summary>
public sealed class WorkRequest
{
    public ChatRequest Chat { get; }
    public WorkItem Item { get; }
    public RequestType Type { get; }
    public int Priority { get; }

    /// <summary>Filled by the evaluator just before the pipeline starts.</summary>
    public RouteDecision Plan { get; set; }

    public WorkRequest(ChatRequest chat, WorkItem item, RequestType type, int priority)
    {
        Chat = chat;
        Item = item;
        Type = type;
        Priority = priority;
        Plan = new RouteDecision(type, PrefillWorker: "", DecodeWorker: null, ReuseStoreState: false, priority);
    }
}

/// <summary>What a phase handler wants the machine to do next.</summary>
public enum PhaseOutcome
{
    /// <summary>Fire the given event (the machine advances to the next state).</summary>
    Fire,
    /// <summary>Suspend until resumed externally (e.g. streaming decode → NotifyStreamComplete).</summary>
    Wait,
    /// <summary>Stop stepping (terminal state reached).</summary>
    Terminal,
}

public readonly record struct PhaseResult(PhaseOutcome Outcome, SchedulerEvent Event)
{
    public static PhaseResult Fire(SchedulerEvent evt) => new(PhaseOutcome.Fire, evt);
    public static PhaseResult Wait => new(PhaseOutcome.Wait, default);
    public static PhaseResult Terminal => new(PhaseOutcome.Terminal, default);
}

/// <summary>Everything a phase handler needs to do its job. Handlers receive
/// services by constructor injection; this carries the per-request state.</summary>
public sealed class PhaseContext
{
    public WorkRequest Request { get; }
    public WorkItem Item { get; }
    public string Worker { get; }
    public RouteDecision Plan => Request.Plan;

    public PhaseContext(WorkRequest request, WorkItem item, string worker)
    {
        Request = request;
        Item = item;
        Worker = worker;
    }
}

/// <summary>
/// A single phase of the v2 pipeline. Implementations are single-responsibility
/// and only depend on the abstractions they need (gateways, proxy, config). The
/// state machine (transition table) decides ordering — not the handlers.
/// </summary>
public interface IPhaseHandler
{
    WorkItemState State { get; }
    Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct);
}

/// <summary>Records the routing decision onto the work item.</summary>
public sealed class RoutePhase : IPhaseHandler
{
    private readonly IReadOnlyList<WorkerConfig> _workers;
    public WorkItemState State => WorkItemState.RouteDecision;

    public RoutePhase(IReadOnlyList<WorkerConfig> workers) => _workers = workers;

    public Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
    {
        var plan = ctx.Plan;
        ctx.Item.PrefillWorker = _workers.FirstOrDefault(w => w.Name == plan.PrefillWorker);
        ctx.Item.DecodeWorker = _workers.FirstOrDefault(w => w.Name == (plan.DecodeWorker ?? plan.PrefillWorker));
        ctx.Item.RequestType = plan.RequestType;
        ctx.Item.PrefixCacheHit = plan.ReuseStoreState;
        ctx.Item.RecordPhase("route_ms");
        return Task.FromResult(PhaseResult.Fire(SchedulerEvent.RouteSucceeded));
    }
}

/// <summary>Runs engine prefill, captures the produced KV blob + n_past.</summary>
public sealed class PrefillPhase : IPhaseHandler
{
    private readonly IEngineRpcGateway _engine;
    public WorkItemState State => WorkItemState.Prefill;

    public PrefillPhase(IEngineRpcGateway engine) => _engine = engine;

    public async Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
    {
        var item = ctx.Item;
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _engine.PrefillAsync(ctx.Worker, ctx.Request.Chat, ct);
            sw.Stop();
            item.EnginePrefillMs = sw.ElapsedMilliseconds;
            item.NPastAfter = result.NPast;
            item.KvBytes = result.StateBytes;
            item.KvBlob = result.KVPayload;
            item.TokensIn = ctx.Request.Chat.EstimatedTokens;
            return PhaseResult.Fire(SchedulerEvent.PrefillSucceeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // transient engine fault → bounded retry (re-route), then fail.
            item.RetryCount++;
            item.Error = ex; // surface the real fault at terminal failure
            return item.RetryCount < WorkItem.MaxRetries
                ? PhaseResult.Fire(SchedulerEvent.Retry)
                : PhaseResult.Fire(SchedulerEvent.Failed);
        }
    }
}

/// <summary>Persists the KV blob produced by prefill to the Store.</summary>
public sealed class SaveKvPhase : IPhaseHandler
{
    private readonly IStoreGateway _store;
    public WorkItemState State => WorkItemState.SaveKv;

    public SaveKvPhase(IStoreGateway store) => _store = store;

    public async Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
    {
        var item = ctx.Item;
        if (item.KvBlob is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        var ok = await _store.PutAsync(item.SessionId, item.KvBlob, ct);
        if (!ok)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        item.KvRestoredForDecode = false;
        item.RecordPhase("save_kv_ms");
        return PhaseResult.Fire(SchedulerEvent.SaveKvSucceeded);
    }
}

/// <summary>Restores the KV onto the decode worker before decoding.</summary>
public sealed class RestorePhase : IPhaseHandler
{
    private readonly IStoreGateway _store;
    private readonly IEngineRpcGateway _engine;
    public WorkItemState State => WorkItemState.RestoreKv;

    public RestorePhase(IStoreGateway store, IEngineRpcGateway engine)
    {
        _store = store;
        _engine = engine;
    }

    public async Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
    {
        var item = ctx.Item;
        var kv = await _store.GetAsync(item.SessionId, ct);
        if (kv is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        if (!await _engine.RestoreAsync(ctx.Worker, item.SessionId, kv, item.NPastAfter, ct))
            return PhaseResult.Fire(SchedulerEvent.Failed);

        item.KvRestoredForDecode = true;
        item.RecordPhase("restore_kv_ms");
        return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);
    }
}

/// <summary>
/// Decodes via the shared HTTP completion proxy. Streaming requests hand the
/// chunk stream to the caller (controller) and suspend until
/// <c>NotifyStreamComplete</c>; non-streaming requests complete inline.
/// </summary>
public sealed class DecodePhase : IPhaseHandler
{
    private readonly ICompletionProxyService _proxy;
    public WorkItemState State => WorkItemState.Decode;

    public DecodePhase(ICompletionProxyService proxy) => _proxy = proxy;

    public async Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
    {
        var item = ctx.Item;
        var workerUrl = ResolveLlamaUrl(ctx.Item.DecodeWorker);
        if (string.IsNullOrEmpty(workerUrl))
            return PhaseResult.Fire(SchedulerEvent.Failed);

        if (ctx.Request.Chat.Stream)
        {
            var stream = _proxy.ProxyCompletionStreamAsync(workerUrl, ctx.Request.Chat.Body, ctx.Request.Chat.TraceId, ct);
            item.DecodeChunks = stream;
            item.StreamCompletion.TrySetResult(stream);
            return PhaseResult.Wait; // resumed by NotifyStreamComplete
        }

        item.Response = await _proxy.ProxyCompletionAsync(workerUrl, ctx.Request.Chat.Body, ctx.Request.Chat.TraceId, ct);
        item.TokensOut = ExtractCompletionTokens(item.Response);
        item.RecordPhase("decode_ms");
        return PhaseResult.Fire(SchedulerEvent.DecodeSucceeded);
    }

    private static string? ResolveLlamaUrl(WorkerConfig? worker)
        => worker is null ? null : worker.LlamaUrl;

    private static int ExtractCompletionTokens(object? response)
    {
        if (response is not Dictionary<string, object> dict) return 0;
        if (!dict.TryGetValue("usage", out var usage) || usage is not Dictionary<string, object> u) return 0;
        return u.TryGetValue("completion_tokens", out var t) && t is JsonElement je && je.ValueKind == JsonValueKind.Number
            ? je.GetInt32()
            : 0;
    }
}

/// <summary>Background-save after a stream ends. No-op for v1 (single-blob save
/// already happened); chunked write-behind is WP3 parity scope.</summary>
public sealed class BgSavePhase : IPhaseHandler
{
    public WorkItemState State => WorkItemState.BgSave;

    public Task<PhaseResult> RunAsync(PhaseContext ctx, CancellationToken ct)
        => Task.FromResult(PhaseResult.Fire(SchedulerEvent.BgSaveSucceeded));
}
