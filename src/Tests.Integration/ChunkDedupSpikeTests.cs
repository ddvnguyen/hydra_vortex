using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Npgsql;
using StoreConfig = Hydra.Store.StoreConfig;
using StoreServer = Hydra.Store.StoreServer;
using StorageEngine = Hydra.Store.StorageEngine;
using ChunkStore = Hydra.Store.ChunkStore;
using ChunkEngine = Hydra.Store.ChunkEngine;
using StoreMetadata = Hydra.Store.StoreMetadata;

namespace Tests.Integration;

/// <summary>
/// Spike / investigation tests for issue #56.
///
/// These tests measure whether llama KV-state serialisation is byte-stable
/// for fixed-offset 1 MB chunking, which is the fundamental assumption
/// M2 delta-save (#58) and prefix warm-start (#59) rely on.
///
/// Run against a live Store + Agent + llama stack:
///   dotnet test --filter "Category=spike" -v normal
///
/// Record the printed dedup ratios in a comment on GitHub issue #56.
/// </summary>
[Collection("SerializedPG")]
[Trait("Category", "spike")]
public sealed class ChunkDedupSpikeTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private StoreServer? _storeServer;
    private StoreMetadata? _metadata;
    private Task? _storeServerTask;
    private Hydra.Store.ChunkStore? _chunkStore;
    private int _storePort;

    public ChunkDedupSpikeTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-dedup-spike-{Guid.NewGuid():N}"));
    }

    public async Task InitializeAsync()
    {
        var cfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            StoreDir = _storeDir.FullName,
        };

        var connStr = Environment.GetEnvironmentVariable("HYDRA_STORE_PG_CONN")
            ?? "Host=localhost;Database=hydra_test;Username=hydra;Password=hydra";
        await EnsureDatabaseAsync(connStr);
        _metadata = new StoreMetadata(connStr);
        await _metadata.EnsureSchemaAsync(CancellationToken.None);
        await using var cleanConn = await _metadata.DataSource.OpenConnectionAsync();
        await using var cleanCmd = cleanConn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
        await cleanCmd.ExecuteNonQueryAsync();

        var engine = new StorageEngine(_storeDir);
        _chunkStore = new Hydra.Store.ChunkStore(_storeDir);
        _storeServer = new StoreServer(cfg, engine, _chunkStore, _metadata);
        _storeServerTask = Task.Run(() => _storeServer.RunAsync(CancellationToken.None));
        await Task.Delay(500);
        _storePort = _storeServer.Port;
    }

    public async Task DisposeAsync()
    {
        if (_storeServer is not null)
            await _storeServer.DisposeAsync();
        if (_metadata is not null)
        {
            await using var cleanConn = await _metadata.DataSource.OpenConnectionAsync();
            await using var cleanCmd = cleanConn.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
            await cleanCmd.ExecuteNonQueryAsync();
            await _metadata.DisposeAsync();
        }
        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies that a re-save after appending tokens reuses most chunks.
    ///
    /// Expected: if llama KV serialisation is append-stable, the second save
    /// should have deduped_chunks ≈ first_save_chunks and new_chunks ≈ delta_chunks.
    ///
    /// VERDICT THRESHOLD: dedup_ratio >= 0.80 → fixed-offset chunking viable.
    /// Below 0.50 → pivot to content-defined (rolling-hash) chunking before #58.
    /// </summary>
    [Fact]
    public async Task DeltaSave_MeasuresRealDedupRatio()
    {
        const string session = "spike-delta";
        const int firstSizeBytes  = 10 * 1024 * 1024;   // 10 MB stand-in for real KV state
        const int appendSizeBytes =  2 * 1024 * 1024;   // 2 MB new tokens

        await using var store = new RpcClient("127.0.0.1", _storePort);

        // First save: 10 MB of deterministic content
        var firstData = MakeStableData(firstSizeBytes, seed: 0x42);
        var nPastJson = Encoding.UTF8.GetBytes("""{"n_past":100}""");
        await store.RequestAsync(OpCode.PutMeta, $"kv/{session}", nPastJson, "spike", CancellationToken.None);

        var firstResp = await store.RequestStreamBodyAsync(
            OpCode.PutChunked, $"kv/{session}",
            new MemoryStream(firstData), firstData.Length,
            "spike", CancellationToken.None);

        var first = ParseChunkMeta(firstResp.Meta!);
        Console.WriteLine($"[SPIKE] First save — total={first.total} new={first.newChunks} deduped={first.deduped}");
        Assert.Equal(first.total, first.newChunks);  // first save: all new

        // Second save: same prefix + 2 MB appended
        var secondData = MakeStableData(firstSizeBytes + appendSizeBytes, seed: 0x42);
        var nPastJson2 = Encoding.UTF8.GetBytes("""{"n_past":200}""");
        await store.RequestAsync(OpCode.PutMeta, $"kv/{session}", nPastJson2, "spike", CancellationToken.None);

        var secondResp = await store.RequestStreamBodyAsync(
            OpCode.PutChunked, $"kv/{session}",
            new MemoryStream(secondData), secondData.Length,
            "spike", CancellationToken.None);

        var second = ParseChunkMeta(secondResp.Meta!);
        Console.WriteLine($"[SPIKE] Second save — total={second.total} new={second.newChunks} deduped={second.deduped}");

        double dedupRatio = second.total > 0 ? (double)second.deduped / second.total : 0;
        Console.WriteLine($"[SPIKE] Dedup ratio = {dedupRatio:P1} (threshold: >=80%)");

        // Verify manifest n_past is correct (tests PR-C fix alongside)
        var manifestResp = await store.RequestAsync(
            OpCode.GetManifest, $"kv/{session}",
            ReadOnlyMemory<byte>.Empty, "spike", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, manifestResp.Status);
        using var doc = JsonDocument.Parse(manifestResp.Payload);
        var savedNpast = doc.RootElement.GetProperty("n_past").GetInt32();
        Console.WriteLine($"[SPIKE] Manifest n_past = {savedNpast} (expected 200)");
        Assert.Equal(200, savedNpast);

        // Assert dedup threshold — adjust if llama serialisation proves non-stable
        Assert.True(dedupRatio >= 0.80,
            $"Dedup ratio {dedupRatio:P1} below 80% threshold. " +
            "If this fails on real llama state, pivot to content-defined chunking before implementing #58.");
    }

    /// <summary>
    /// Verifies that two sessions with the same system prompt share prefix chunks.
    ///
    /// Expected: the first K = floor(system_prompt_bytes / CHUNK_SIZE) chunks
    /// of both sessions have identical hashes.
    ///
    /// VERDICT: any shared chunks → cross-session dedup viable for prefix checkpoints.
    /// </summary>
    [Fact]
    public async Task CrossSession_SharedSystemPrompt_SharesPrefixChunks()
    {
        const string sessionA = "spike-prefix-a";
        const string sessionB = "spike-prefix-b";
        const int systemPromptBytes = 3 * 1024 * 1024;  // 3 MB shared prefix
        const int uniqueSuffixBytes =  2 * 1024 * 1024;  // 2 MB unique per session

        await using var store = new RpcClient("127.0.0.1", _storePort);

        // Session A: shared prefix + unique suffix (seed A)
        var dataA = Concat(MakeStableData(systemPromptBytes, seed: 0x10), MakeStableData(uniqueSuffixBytes, seed: 0xAA));
        await SaveChunked(store, sessionA, dataA, nPast: 50);

        // Session B: same shared prefix + different unique suffix (seed B)
        var dataB = Concat(MakeStableData(systemPromptBytes, seed: 0x10), MakeStableData(uniqueSuffixBytes, seed: 0xBB));
        await SaveChunked(store, sessionB, dataB, nPast: 50);

        // Load manifests and compare prefix chunk hashes
        var manifestA = await LoadManifestChunks(store, sessionA);
        var manifestB = await LoadManifestChunks(store, sessionB);

        int prefixChunks = systemPromptBytes / ChunkEngine.CHUNK_SIZE;
        int sharedCount = 0;
        for (int i = 0; i < prefixChunks && i < manifestA.Count && i < manifestB.Count; i++)
        {
            if (manifestA[i] == manifestB[i])
                sharedCount++;
        }

        double shareRatio = prefixChunks > 0 ? (double)sharedCount / prefixChunks : 0;
        Console.WriteLine($"[SPIKE] Cross-session prefix: {sharedCount}/{prefixChunks} chunks shared ({shareRatio:P1})");

        Assert.True(sharedCount > 0,
            "No prefix chunks shared between sessions with identical system prompt. " +
            "Fixed-offset dedup will not work for cross-session prefix checkpoints.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static byte[] MakeStableData(int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        rng.NextBytes(data);
        return data;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static (int total, int newChunks, int deduped) ParseChunkMeta(string meta)
    {
        using var doc = JsonDocument.Parse(meta);
        var root = doc.RootElement;
        return (
            root.GetProperty("total_chunks").GetInt32(),
            root.GetProperty("new_chunks").GetInt32(),
            root.GetProperty("deduped_chunks").GetInt32()
        );
    }

    private async Task SaveChunked(RpcClient store, string session, byte[] data, int nPast)
    {
        var nPastJson = Encoding.UTF8.GetBytes($"{{\"n_past\":{nPast}}}");
        await store.RequestAsync(OpCode.PutMeta, $"kv/{session}", nPastJson, "spike", CancellationToken.None);
        await store.RequestStreamBodyAsync(
            OpCode.PutChunked, $"kv/{session}",
            new MemoryStream(data), data.Length,
            "spike", CancellationToken.None);
    }

    private static async Task<List<string>> LoadManifestChunks(RpcClient store, string session)
    {
        var resp = await store.RequestAsync(
            OpCode.GetManifest, $"kv/{session}",
            ReadOnlyMemory<byte>.Empty, "spike", CancellationToken.None);

        if (resp.Status != (byte)StatusCode.Ok || resp.Payload is null || resp.Payload.Length == 0)
            return [];

        using var doc = JsonDocument.Parse(resp.Payload);
        var chunks = doc.RootElement.GetProperty("chunks");
        return chunks.EnumerateArray()
            .OrderBy(c => c.GetProperty("index").GetInt32())
            .Select(c => c.GetProperty("hash").GetString() ?? "")
            .ToList();
    }

    private static async Task EnsureDatabaseAsync(string connStr)
    {
        var adminConnStr = connStr.Replace("Database=hydra_test", "Database=postgres");
        await using var ds = new NpgsqlDataSourceBuilder(adminConnStr).Build();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE DATABASE hydra_test";
        try { await cmd.ExecuteNonQueryAsync(); } catch (PostgresException ex) when (ex.SqlState == "42P04") { }
    }
}
