namespace Hydra.Core;

/// <summary>
/// Two-tier chunk cache configuration. Bound by env vars
/// (HYDRA_CHUNK_CACHE_* / HYDRA_COORD_CHUNK_CACHE_L1_*).
/// </summary>
public sealed record ChunkCacheConfig
{
    public string L1Dir { get; init; } = EnvString(
        "HYDRA_COORD_CHUNK_CACHE_L1_DIR", "/mnt/llm-ram/chunk-cache-l1");
    public long L1MaxBytes { get; init; } = EnvLong(
        "HYDRA_COORD_CHUNK_CACHE_L1_MAX_BYTES", 20L * 1024 * 1024 * 1024);

    /// <summary>"pg" | "fs". Default "pg" once this lands; "fs" disables L2 (L1 only).</summary>
    public string L2Backend { get; init; } = EnvString(
        "HYDRA_CHUNK_CACHE_BACKEND", "pg");

    public string L2PgConn { get; init; } = EnvString(
        "HYDRA_CHUNK_CACHE_PG_CONN",
        "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra");
    public long L2MaxBytes { get; init; } = EnvLong(
        "HYDRA_CHUNK_CACHE_L2_MAX_BYTES", 50L * 1024 * 1024 * 1024);
    public double L2LowWater { get; init; } = EnvDouble(
        "HYDRA_CHUNK_CACHE_L2_LOW_WATER", 0.9);
    public int L2GcIntervalSeconds { get; init; } = EnvInt(
        "HYDRA_CHUNK_CACHE_L2_GC_INTERVAL_SECONDS", 300);
    public int L2VacuumIntervalSeconds { get; init; } = EnvInt(
        "HYDRA_CHUNK_CACHE_L2_VACUUM_INTERVAL_SECONDS", 3600);
    public int L2BatchSize { get; init; } = EnvInt(
        "HYDRA_CHUNK_CACHE_L2_BATCH_SIZE", 500);

    private static string EnvString(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static int EnvInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
    private static long EnvLong(string key, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
    private static double EnvDouble(string key, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(key),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
