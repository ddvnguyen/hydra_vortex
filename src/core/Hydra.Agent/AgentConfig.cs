namespace Hydra.Agent;

public sealed record AgentConfig
{
    public string Host { get; init; } = EnvString("HYDRA_AGENT_HOST", "0.0.0.0");
    public int Port { get; init; } = EnvInt("HYDRA_AGENT_PORT", 9601);
    public string NodeName { get; init; } = EnvString("HYDRA_AGENT_NODE_NAME", "rtx");
    public string LlamaUrl { get; init; } = EnvString("HYDRA_AGENT_LLAMA_URL", "http://localhost:8080");
    public string StoreHost { get; init; } = EnvString("HYDRA_AGENT_STORE_HOST", "127.0.0.1");
    public int StorePort { get; init; } = EnvInt("HYDRA_AGENT_STORE_PORT", 9500);
    public string SlotSavePath { get; init; } = EnvString("HYDRA_AGENT_SLOT_SAVE_PATH", "/tmp/hydra-kv");
    public string ChunkCacheDir { get; init; } = EnvString("HYDRA_AGENT_CHUNK_CACHE_DIR", "/tmp/hydra-chunk-cache");
    public int DebugHttpPort { get; init; } = EnvInt("HYDRA_AGENT_DEBUG_HTTP_PORT", 9611);

    public void Validate()
    {
        if (Port < 1 || Port > 65535)
            throw new InvalidOperationException($"Invalid port: {Port}");
        if (DebugHttpPort < 1 || DebugHttpPort > 65535)
            throw new InvalidOperationException($"Invalid debug HTTP port: {DebugHttpPort}");
        if (DebugHttpPort == Port)
            throw new InvalidOperationException("Debug HTTP port must differ from RPC port");

        if (!Uri.TryCreate(LlamaUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Invalid Llama URL: {LlamaUrl}");

        var cacheDir = new DirectoryInfo(ChunkCacheDir);
        if (!cacheDir.Exists)
            throw new InvalidOperationException($"Chunk cache directory does not exist: {ChunkCacheDir}");

        var saveDir = new DirectoryInfo(SlotSavePath);
        if (!saveDir.Exists)
            throw new InvalidOperationException($"Slot save path directory does not exist: {SlotSavePath}");
    }

    private static string EnvString(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
