using System.Text.Json.Serialization;

namespace Hydra.Store;

public sealed record StoreConfig
{
    public string Host { get; init; } = EnvString("HYDRA_STORE_HOST", "0.0.0.0");
    public int Port { get; init; } = EnvInt("HYDRA_STORE_PORT", 9500);
    public string StoreDir { get; init; } = EnvString("HYDRA_STORE_DIR", "/mnt/llm-ram/store");
    public long MaxPayloadBytes { get; init; } = 4_294_967_296;
    public int DebugHttpPort { get; init; } = EnvInt("HYDRA_STORE_DEBUG_PORT", 9501);
    public string PgConn { get; init; } = EnvString("HYDRA_STORE_PG_CONN",
        "Host=postgres;Database=hydra_store;Username=hydra;Password=hydra");
    public string BackupDir { get; init; } = EnvString("HYDRA_STORE_BACKUP_DIR", "/mnt/SSD/hydra-backup");
    public int RestoreTopN { get; init; } = EnvInt("HYDRA_STORE_RESTORE_TOP_N", 10);

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

        if (!Directory.Exists(StoreDir))
            throw new InvalidOperationException($"Store directory does not exist: {StoreDir}");

        if (string.IsNullOrWhiteSpace(PgConn))
            throw new InvalidOperationException("PG connection string is required");
    }

    private static string EnvString(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
