using Hydra.Core;
using Hydra.Core.Models;

namespace Tests.Core;

public sealed class CoordinatorConfigTests
{
    [Fact]
    public void Validate_NoWorkers_Throws()
    {
        var cfg = new CoordinatorConfig();
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_ValidWorkers_DoesNotThrow()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3 });
        cfg.Validate(); // no throw
    }

    [Fact]
    public void Validate_EmptyWorkerName_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_MissingHost_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", RpcPort = 9601, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_InvalidPort_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 0, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_InvalidLlamaUrl_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "not-a-url" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Defaults_Are_Reasonable()
    {
        var cfg = new CoordinatorConfig();
        Assert.Equal(9000, cfg.Port);
        Assert.Equal("concurrency", cfg.RunMode);
        Assert.False(cfg.MixPrecisionEnabled);
        Assert.Equal(2048, cfg.AtomicThreshold);
        Assert.Equal(5120, cfg.WarmThreshold);
        Assert.Equal(1800, cfg.LlamaRequestTimeoutS);
        Assert.Equal(50, cfg.NPastGuardTolerance);
    }

    [Fact]
    public void AtomicThreshold_NewEnvVar_Wins()
    {
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", "777");
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", "111");
        try
        {
            Assert.Equal(777, new CoordinatorConfig().AtomicThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", null);
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", null);
        }
    }

    [Fact]
    public void AtomicThreshold_LegacyEnvVar_FallsBack()
    {
        // Back-compat: the legacy HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD still applies
        // when the new HYDRA_COORD_ATOMIC_THRESHOLD is unset.
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", null);
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", "333");
        try
        {
            Assert.Equal(333, new CoordinatorConfig().AtomicThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", null);
        }
    }

    [Fact]
    public void NPastGuardTolerance_FromEnv()
    {
        Environment.SetEnvironmentVariable("HYDRA_COORD_N_PAST_GUARD_TOLERANCE", "2048");
        try
        {
            Assert.Equal(2048, new CoordinatorConfig().NPastGuardTolerance);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_N_PAST_GUARD_TOLERANCE", null);
        }
    }

    [Fact]
    public void Worker_CanPrefill_Mixed()
    {
        var w = new WorkerConfig { WorkerType = 3 };
        Assert.True(w.CanPrefill);
        Assert.True(w.CanDecode);
    }

    [Fact]
    public void Worker_CanPrefill_OnlyPrefill()
    {
        var w = new WorkerConfig { WorkerType = 1 };
        Assert.True(w.CanPrefill);
        Assert.False(w.CanDecode);
    }

    [Fact]
    public void Worker_CanDecode_OnlyDecode()
    {
        var w = new WorkerConfig { WorkerType = 2 };
        Assert.False(w.CanPrefill);
        Assert.True(w.CanDecode);
    }

    // ── LoadWorkers() ────────────────────────────────────────────────
    //
    // These tests pin the precedence rules:
    //   1. HYDRA_COORD_CONFIG_FILE — canonical (file path), parsed as
    //      snake_case JSON. Throw with a clear message if the file is
    //      missing or unparseable.
    //   2. HYDRA_COORD_WORKERS — legacy inline JSON env, used only when
    //      the file env is unset. Kept for unit-test convenience and
    //      ad-hoc local runs.
    //   3. Fallback hard-coded list — only when neither env is set
    //      (test harnesses that don't care about real config).

    [Fact]
    public void LoadWorkers_FromFile_LoadsConfig()
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, """
            [
              {"name": "test_node", "host": "localhost", "rpc_port": 9999,
               "llama_url": "http://localhost:8080", "worker_type": 3, "slots": 1,
               "prefill_priority": 1, "decode_priority": 1, "decode_speed_tps": 30}
            ]
            """);

        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", tmpFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", null);

            var workers = CoordinatorConfig.LoadWorkers();
            Assert.Single(workers);
            Assert.Equal("test_node", workers[0].Name);
            Assert.Equal(9999, workers[0].RpcPort);
            Assert.Equal(3, workers[0].WorkerType);
        }
        finally
        {
            File.Delete(tmpFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_FilePathSetButMissing_ThrowsClearError()
    {
        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", "/nonexistent/path/workers.json");
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", null);

            var ex = Assert.Throws<InvalidOperationException>(() => CoordinatorConfig.LoadWorkers());
            Assert.Contains("/nonexistent/path/workers.json", ex.Message);
            Assert.Contains("does not exist", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_FileMalformedJson_ThrowsWithPathContext()
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, "{ this is not valid JSON ");

        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", tmpFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", null);

            var ex = Assert.Throws<InvalidOperationException>(() => CoordinatorConfig.LoadWorkers());
            Assert.Contains(tmpFile, ex.Message);
            Assert.Contains("Failed to parse", ex.Message);
        }
        finally
        {
            File.Delete(tmpFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_FileTakesPrecedence_OverInlineEnv()
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, """
            [{"name": "from_file", "host": "h", "rpc_port": 1, "llama_url": "http://x", "worker_type": 3}]
            """);

        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", tmpFile);
            // Inline env has a different worker name; file should win.
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS",
                """[{"name":"from_env","host":"h","rpc_port":2,"llama_url":"http://x","worker_type":3}]""");

            var workers = CoordinatorConfig.LoadWorkers();
            Assert.Single(workers);
            Assert.Equal("from_file", workers[0].Name);
            Assert.Equal(1, workers[0].RpcPort);
        }
        finally
        {
            File.Delete(tmpFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_InlineJsonEnv_StillWorks_WhenNoFile()
    {
        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", null);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS",
                """[{"name":"legacy","host":"h","rpc_port":42,"llama_url":"http://x","worker_type":3}]""");

            var workers = CoordinatorConfig.LoadWorkers();
            Assert.Single(workers);
            Assert.Equal("legacy", workers[0].Name);
            Assert.Equal(42, workers[0].RpcPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_NoEnvAtAll_UsesDefaultFallback()
    {
        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", null);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", null);

            var workers = CoordinatorConfig.LoadWorkers();
            Assert.Equal(2, workers.Count);
            Assert.Contains(workers, w => w.Name == "rtx");
            Assert.Contains(workers, w => w.Name == "p100");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }

    [Fact]
    public void LoadWorkers_ProductionConfigFile_LoadsBothWorkersWithCorrectModelFields()
    {
        // Pin the live production file (committed to infra/hydra-core/config/workers.json).
        // Asserts the shape: BOTH workers have all model_* fields NULL — interpretation (b) of the
        // mix-precision P/D split (same model across prefill and decode phases; the cross-model
        // guard in WorkerSchedulerService.RestoreKvAsync accepts the restore when both slots
        // load the same GGUF file, which produces the same model_hash). Pre-#289 / pre-PR-292
        // the RTX had prefill_model_name=mini, decode_model_name=balanced; that configuration
        // was mathematically broken (Q3_K KV != Q5_K weights) and is no longer supported by
        // default. See docs/workflow/08-llama-fork.md and the cross-model safety section in
        // specs/rpc-protocol.md for the full reasoning.
        // If this test breaks, the compose deployment will break.
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var configPath = Path.Combine(repoRoot, "infra", "hydra-core", "config", "workers.json");
        if (!File.Exists(configPath))
            return; // skip if running outside the repo (e.g. CI in a stripped tree)

        var prevFile = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
        var prevJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", configPath);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", null);

            var workers = CoordinatorConfig.LoadWorkers();
            Assert.Equal(3, workers.Count);

            var rtx = workers.Single(w => w.Name == "rtx");
            Assert.Equal(3, rtx.WorkerType);
            Assert.Null(rtx.RouterModelName);
            Assert.Null(rtx.PrefillModelName);
            Assert.Null(rtx.DecodeModelName);

            var rtx3060 = workers.Single(w => w.Name == "rtx3060");
            Assert.Equal(3, rtx3060.WorkerType);
            Assert.Null(rtx3060.RouterModelName);
            Assert.Null(rtx3060.PrefillModelName);
            Assert.Null(rtx3060.DecodeModelName);

            var p100 = workers.Single(w => w.Name == "p100");
            Assert.Equal(2, p100.WorkerType);
            Assert.Null(p100.RouterModelName);
            Assert.Null(p100.PrefillModelName);
            Assert.Null(p100.DecodeModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE", prevFile);
            Environment.SetEnvironmentVariable("HYDRA_COORD_WORKERS", prevJson);
        }
    }
}
