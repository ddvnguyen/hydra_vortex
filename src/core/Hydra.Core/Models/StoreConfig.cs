using System.Text.Json.Serialization;

namespace Hydra.Core;

public sealed record StoreConfig
{
    public string Host { get; init; } = EnvString("HYDRA_STORE_HOST", "0.0.0.0");
    public int Port { get; init; } = EnvInt("HYDRA_STORE_PORT", 9500);
    public string NodeName { get; init; } = EnvString("HYDRA_STORE_NODE_NAME", "");
    public string StoreDir { get; init; } = EnvString("HYDRA_STORE_DIR", "/mnt/llm-ram/store");
    public long MaxPayloadBytes { get; init; } = 4_294_967_296;
    public int DebugHttpPort { get; init; } = EnvInt("HYDRA_STORE_DEBUG_PORT", 9501);
    public string PgConn { get; init; } = EnvString("HYDRA_STORE_PG_CONN",
        EnvString("ConnectionStrings__hydra-store",
        "Host=postgres;Database=hydra_store;Username=hydra;Password=hydra"));
    public string BackupDir { get; init; } = EnvString("HYDRA_STORE_BACKUP_DIR", "/mnt/SSD/hydra-backup");
    public int RestoreTopN { get; init; } = EnvInt("HYDRA_STORE_RESTORE_TOP_N", 10);

    /// <summary>
    /// RAM-front recency window (hours) for the SSD-durable store tier (#470).
    /// Once a chunk or per-session <c>sess_*.kv</c> manifest is backed up to
    /// SSD (and marked in PG), the write-behind service may evict the tmpfs
    /// RAM copy when no session referencing it has been updated within this
    /// window (same freshness semantics as the retention GC, so warm/recent
    /// sessions are never victims). <c>0</c> disables free-after-backup
    /// entirely (backward compatible — files stay on RAM until retention GC).
    /// </summary>
    public int RamKeepRecentHours { get; init; } = EnvInt("HYDRA_STORE_RAM_KEEP_RECENT_HOURS", 1);

    /// <summary>
    /// Retention TTL (hours) for saved-KV sessions. A session whose
    /// <c>sessions.updated_at</c> is older than this — and the chunks it
    /// references, once no other session references them — is evicted from
    /// the tmpfs chunk dir, the SSD backup dir, and PG. <c>0</c> disables
    /// retention GC entirely (#470 post-fix queue #1). Freshness is
    /// <c>updated_at</c>, bumped on every save, so an actively used session
    /// (incl. warm slots restored and re-saved each turn) is never a victim.
    /// </summary>
    public int ChunkRetentionTtlHours { get; init; } = EnvInt("HYDRA_STORE_RETENTION_TTL_HOURS", 168);

    [JsonIgnore]
    public DirectoryInfo StoreDirectory => new(StoreDir);

    public void Validate()
    {
        if (Port < 1 || Port > 65535)
            throw new InvalidOperationException($"Invalid port: {Port}");
        if (DebugHttpPort < 1 || DebugHttpPort > 65535)
            throw new InvalidOperationException($"Invalid debug HTTP port: {DebugHttpPort}");
        if (DebugHttpPort == Port)
            throw new InvalidOperationException("Debug HTTP port must differ from RPC port");

        if (string.IsNullOrWhiteSpace(PgConn))
            throw new InvalidOperationException("PG connection string is required");
        // StoreDir not checked — auto-created by StorageEngine/ChunkStore constructors.
    }

    private static string EnvString(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
