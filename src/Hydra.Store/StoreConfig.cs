using System.Text.Json.Serialization;

namespace Hydra.Store;

public sealed record StoreConfig
{
    public string Host { get; init; } = EnvString("HYDRA_STORE_HOST", "0.0.0.0");
    public int Port { get; init; } = EnvInt("HYDRA_STORE_PORT", 9500);
    public string StoreDir { get; init; } = EnvString("HYDRA_STORE_DIR", "/mnt/llm-ram/store");
    public long MaxPayloadBytes { get; init; } = 4_294_967_296;
    public int DebugHttpPort { get; init; } = EnvInt("HYDRA_STORE_DEBUG_PORT", 9501);

    [JsonIgnore]
    public DirectoryInfo StoreDirectory => new(StoreDir);

    private static string EnvString(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
