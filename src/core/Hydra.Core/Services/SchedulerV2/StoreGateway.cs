using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Shared;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// KV store persistence for the v2 scheduler. Single responsibility: put/get the
/// per-session KV blob, plus the content-addressed chunked/delta-save primitives
/// (SYNC_MISSING / PUSH_CHUNKS / PUT_MANIFEST). The interface hides the transport
/// so the chunked flow can be swapped or fault-injected behind it.
/// </summary>
public interface IStoreGateway
{
    Task<bool> PutAsync(string sessionId, ReadOnlyMemory<byte> kv, CancellationToken ct);
    Task<byte[]?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>Raw store Get by exact key (prefix checkpoints use <c>prefix/{hash}.kv</c>).</summary>
    Task<byte[]?> GetRawAsync(string key, CancellationToken ct);

    /// <summary>Raw store GET_MANIFEST by exact key — the JSON payload carries
    /// <c>{"n_past":N, "chunks":[...]}</c> for the prefix n_past guard.</summary>
    Task<byte[]?> GetManifestAsync(string key, CancellationToken ct);

    /// <summary>SYNC_MISSING (0x12): ask the Store which of the given chunk hashes
    /// it lacks. Returns the missing set (empty = every chunk already resident).
    /// The hashes are the chunk list's ordered SHA-256 hex hashes (delta-save
    /// step 1 — legacy <c>SyncMissingAsync</c>, WorkerSchedulerService.cs:4183).</summary>
    Task<List<string>> SyncMissingAsync(string storeKey, IReadOnlyList<string> hashes, CancellationToken ct);

    /// <summary>PUSH_CHUNKS (0x13): upload the chunk bodies the Store reported
    /// missing, framed [4B size LE][body] and batched at 32 MB (peak memory
    /// bounded regardless of state size — legacy <c>PushMissingChunksAsync</c>,
    /// WorkerSchedulerService.cs:4203). Returns the number of chunks pushed
    /// (0 when nothing was missing).</summary>
    Task<int> PushChunksAsync(string storeKey, IReadOnlyList<string> missing, IReadOnlyList<ChunkRef> allChunks, byte[] stateData, CancellationToken ct);

    /// <summary>PUT_MANIFEST (0x15): write the authoritative ordered manifest
    /// (<c>{"n_past":N,"total_size":T, model identity...,"chunks":[{"index",...}]}</c>).
    /// The model identity (M-Perf.9 #289/#470) lets the cross-model guard survive a
    /// Coordinator restart; an empty identity writes ""/0 (pre-#470 back-compat).
    /// Legacy <c>PutManifestAsync</c>, WorkerSchedulerService.cs:4349.</summary>
    Task PutManifestAsync(string storeKey, int nPast, long totalSize, IReadOnlyList<ChunkRef> chunks, CancellationToken ct, ModelIdentity? identity = null);
}

public static class StoreKeys
{
    /// <summary>The store key for a session's KV blob (<c>{sessionId}.kv</c> — the
    /// wire key the goldens pin).</summary>
    public static string KvKey(string sessionId) => $"{sessionId}.kv";
}

public sealed class StoreGateway : IStoreGateway
{
    /// <summary>PUSH_CHUNKS flush threshold (legacy 32 MB): chunk bodies are framed
    /// into one RPC payload until the batch crosses this, bounding peak memory.</summary>
    private const int PushChunksBatchBytes = 32 * 1024 * 1024;

    private readonly RpcClient _store;

    public StoreGateway(RpcClient store) => _store = store;

    public async Task<bool> PutAsync(string sessionId, ReadOnlyMemory<byte> kv, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(OpCode.Put, sessionId, kv, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok;
    }

    public async Task<byte[]?> GetAsync(string sessionId, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(OpCode.Get, sessionId, ReadOnlyMemory<byte>.Empty, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }

    public Task<byte[]?> GetRawAsync(string key, CancellationToken ct)
        => GetRawAsyncCore(key, OpCode.Get, ct);

    public Task<byte[]?> GetManifestAsync(string key, CancellationToken ct)
        => GetRawAsyncCore(key, OpCode.GetManifest, ct);

    public async Task<List<string>> SyncMissingAsync(string storeKey, IReadOnlyList<string> hashes, CancellationToken ct)
    {
        // Delta-save step 1: send the full ordered hash list, get back the subset
        // the Store lacks (payload: JSON {"missing_hashes":[...]}).
        var payload = JsonSerializer.SerializeToUtf8Bytes(hashes);
        var resp = await _store.RequestAsync(OpCode.SyncMissing, storeKey, payload, "v2-store", ct);
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

    public async Task<int> PushChunksAsync(
        string storeKey, IReadOnlyList<string> missing, IReadOnlyList<ChunkRef> allChunks,
        byte[] stateData, CancellationToken ct)
    {
        if (missing.Count == 0) return 0;

        // Delta-save step 2: upload only the missing chunk bodies, framed
        // [4B size LE][body], batched so peak memory is bounded regardless of
        // total state size. A non-Ok status surfaces the real root cause BEFORE
        // PUT_MANIFEST runs — a manifest referencing unresident chunks would
        // otherwise be written over a half-pushed state (#336).
        using var batch = new MemoryStream();
        int pending = 0;   // chunks buffered in the current (unflushed) batch
        int pushedOk = 0;  // chunks successfully flushed in prior batches
        async Task FlushAsync()
        {
            if (batch.Length == 0) return;
            var resp = await _store.RequestAsync(OpCode.PushChunks, storeKey, batch.ToArray(), "v2-store", ct);
            if (resp.Status != (byte)StatusCode.Ok)
                throw new InvalidDataException($"PUSH_CHUNKS failed (status=0x{resp.Status:X2}): {resp.Meta}");
            pushedOk += pending;
            pending = 0;
            batch.SetLength(0);
        }

        // ChunkRef.Index i ⇒ bytes [i*CHUNK_SIZE, …] — exactly how ChunkAndHash
        // built the list, so the offset/size math reconstructs each body.
        var chunkSize = ChunkEngine.CHUNK_SIZE;
        var header = new byte[4];
        foreach (var hash in missing)
        {
            var chunkRef = allChunks.FirstOrDefault(c => c.Hash == hash);
            if (chunkRef is null) continue;
            var offset = chunkRef.Index * chunkSize;
            var size = Math.Min(chunkSize, stateData.Length - offset);
            if (size <= 0) continue;
            BinaryPrimitives.WriteInt32LittleEndian(header, size);
            batch.Write(header);
            batch.Write(stateData, offset, size);
            pending++;
            if (batch.Length >= PushChunksBatchBytes) await FlushAsync();
        }
        await FlushAsync();
        return pushedOk;
    }

    public async Task PutManifestAsync(
        string storeKey, int nPast, long totalSize, IReadOnlyList<ChunkRef> chunks,
        CancellationToken ct, ModelIdentity? identity = null)
    {
        // Delta-save step 3: the authoritative ordered manifest. Field set + order
        // match the legacy PutManifestAsync EXACTLY (the golden pins the payload
        // LENGTH, which depends on this serialization). M-Perf.9 #289/#470: persist
        // the slot's model identity so the cross-model guard survives a restart;
        // pre-#470 callers pass an empty identity and the guard skips.
        var model = identity ?? ModelIdentity.Empty;
        var manifest = new
        {
            n_past = nPast,
            total_size = totalSize,
            model_alias = "",
            tokenizer = model.Tokenizer,
            model_name = model.ModelName,
            model_quant = model.ModelQuant,
            model_capabilities = model.ModelCapabilities,
            model_path = "",
            chunks = chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size }),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var resp = await _store.RequestAsync(OpCode.PutManifest, storeKey, payload, "v2-store", ct);
        if (resp.Status != (byte)StatusCode.Ok)
            throw new InvalidDataException($"PUT_MANIFEST failed (status=0x{resp.Status:X2}): {resp.Meta}");
    }

    private async Task<byte[]?> GetRawAsyncCore(string key, OpCode op, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(op, key, ReadOnlyMemory<byte>.Empty, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }
}
