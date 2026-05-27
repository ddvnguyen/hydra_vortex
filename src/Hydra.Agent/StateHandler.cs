using System.Diagnostics;
using Hydra.Shared;
using Serilog;

namespace Hydra.Agent;

public sealed record SaveResult(
    string SessionId,
    int SlotId,
    int NPast,
    long Size,
    long ElapsedMs
);

public sealed record RestoreSessionResult(
    string SessionId,
    int SlotId,
    bool Restored,
    int NPast,
    long ElapsedMs
);

public sealed class StateHandler
{
    private readonly LlamaClient _llama;
    private readonly RpcClient _store;
    private readonly ILogger _log;

    public StateHandler(LlamaClient llama, RpcClient store, ILogger log)
    {
        _llama = llama;
        _store = store;
        _log = log;
    }

    public async Task<SaveResult> SaveToStoreAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var meta = await _llama.GetStateMetaAsync(slotId, ct);
        var stateStream = await _llama.GetStateAsync(slotId, ct);
        var stateSize = meta.StateSize;

        _log.Information("Saving session {SessionId} slot {SlotId} state (size={Size}) to store",
            sessionId, slotId, stateSize);

        await _store.RequestStreamBodyAsync(
            OpCode.Put, $"kv/{sessionId}", stateStream, stateSize,
            traceId, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Saved session {SessionId} slot {SlotId} to store in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new SaveResult(sessionId, slotId, meta.NPast, stateSize, elapsed);
    }

     public async Task<RestoreSessionResult> RestoreFromStoreAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var getResp = await _store.RequestAsync(
            OpCode.Get, $"kv/{sessionId}", ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        if (getResp.Status != (byte)StatusCode.Ok)
        {
            _log.Error("Session {SessionId} not found in store", sessionId);
            throw new InvalidDataException(
                $"Session '{sessionId}' not found in store (status=0x{getResp.Status:X2})");
        }

        var longContentLength = 0L;
        if (getResp.Meta is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(getResp.Meta);
            if (doc.RootElement.TryGetProperty("size", out var sizeEl))
                longContentLength = sizeEl.GetInt64();
        }

        _log.Information("Restoring session {SessionId} to slot {SlotId} (size={Size})",
            sessionId, slotId, longContentLength);

        // Use RequestStreamAsync for the actual data transfer now that we have the length
        var storeStream = _store.RequestStreamAsync(
            OpCode.Get, $"kv/{sessionId}", ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        var asyncStream = new AsyncEnumerableStream(storeStream, ct);

        var result = await _llama.PutStateAsync(slotId, asyncStream, longContentLength, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Restored session {SessionId} to slot {SlotId} in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new RestoreSessionResult(sessionId, slotId, result.Restored, result.NPast, elapsed);
    }
}

internal struct ValueStopwatch
{
    private readonly long _start;

    private ValueStopwatch(long start) => _start = start;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    public long ElapsedMilliseconds
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _start;
            return elapsed * 1000 / Stopwatch.Frequency;
        }
    }
}
