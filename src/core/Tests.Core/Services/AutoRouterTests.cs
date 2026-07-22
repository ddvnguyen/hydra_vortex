using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Tests.Core;

namespace Tests.Core.Services;

public class AutoRouterTests
{
    // Fake slot verification — always returns true (slot is warm and valid).
    private static readonly Func<WorkerConfig, SessionEntry, string, Task<bool>> FakeVerifyWarm = (_, _, _) => Task.FromResult(true);

    // Fake slot verification — returns false (slot probe failed).
    private static readonly Func<WorkerConfig, SessionEntry, string, Task<bool>> FakeVerifyFailed = (_, _, _) => Task.FromResult(false);
    private static CoordinatorConfig MakeConfig() => new()
    {
        WarmSlotVerificationEnabled = false,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
                LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
                Role = "head", PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", Host = "192.168.122.21", RpcPort = 9602,
                LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1,
                PrefillPriority = 100, DecodePriority = 1 },
        },
    };

    private static ModelConfigLoader MakeLoader()
    {
        var models = new Dictionary<string, ModelTemplate>
        {
            ["moe-35b-solo"] = new ModelTemplate
            {
                Description = "solo",
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-mini",
                LoadTimeS = 40,
                QualityTier = 1,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = GpuCapabilities.FlashAttn,
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 0,
                    MaxPromptTokens = 2048,
                    MaxContextTokens = 128000,
                },
            },
            ["moe-35b-pd"] = new ModelTemplate
            {
                Description = "pd",
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-balanced",
                LoadTimeS = 40,
                QualityTier = 2,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = GpuCapabilities.FlashAttn,
                    DecodeRequirements = new ModelRequirements
                    {
                        MinVramMb = 16000,
                        RequiredCapabilities = GpuCapabilities.FlashAttn,
                    },
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 2048,
                    MaxPromptTokens = 999999,
                    MaxContextTokens = 128000,
                    RequiresWorkers = ["p100"],
                },
            },
        };
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = models,
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"]     = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                ["qwen3.6-35B-balanced"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
                ["qwen3.6-27B-coder"]    = "Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            },
        };
        ModelConfigLoader.Reset();
        ModelConfigLoader.SetInstance(ModelConfigLoader.Create(config));
        return ModelConfigLoader.Instance;
    }

    // ── Config/Loader helpers for default_eligible tests ──────────────

    private static CoordinatorConfig MakeConfigWithCombined()
    {
        var cfg = MakeConfig();
        cfg.Workers = new List<WorkerConfig>(cfg.Workers)
        {
            new() { Name = "rtx3060", Host = "localhost", RpcPort = 9603,
                LlamaUrl = "http://localhost:9504", WorkerType = 3, Slots = 1,
                Role = "head", PrefillPriority = 2, DecodePriority = 3,
                CombinedCapable = true },
        };
        return cfg;
    }

    private static ModelConfigLoader MakeLoaderWithCombined()
    {
        var models = new Dictionary<string, ModelTemplate>
        {
            ["moe-35b-solo"] = new ModelTemplate
            {
                Description = "solo",
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-mini",
                LoadTimeS = 40,
                QualityTier = 1,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = GpuCapabilities.FlashAttn,
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 0,
                    MaxPromptTokens = 2048,
                    MaxContextTokens = 128000,
                },
            },
            ["moe-35b-pd"] = new ModelTemplate
            {
                Description = "pd",
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-balanced",
                LoadTimeS = 40,
                QualityTier = 2,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = GpuCapabilities.FlashAttn,
                    DecodeRequirements = new ModelRequirements
                    {
                        MinVramMb = 16000,
                        RequiredCapabilities = GpuCapabilities.FlashAttn,
                    },
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 2048,
                    MaxPromptTokens = 999999,
                    MaxContextTokens = 128000,
                    RequiresWorkers = ["p100"],
                },
            },
            ["dense-27b-combined"] = new ModelTemplate
            {
                Description = "combined",
                PrefillAlias = "qwen3.6-27B-coder",
                DecodeAlias  = "qwen3.6-27B-coder",
                LoadTimeS = 45,
                QualityTier = 3,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 12000,
                    RequiredCapabilities = GpuCapabilities.Combined,
                    PeerRequirements = new ModelRequirements
                    {
                        MinVramMb = 12000,
                        RequiredCapabilities = GpuCapabilities.Combined,
                    },
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    DefaultEligible = false,
                    MinPromptTokens = 0,
                    MaxPromptTokens = 999999,
                    MaxContextTokens = 128000,
                    RequiresWorkers = ["rtx3060"],
                },
            },
        };
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = models,
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"]     = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                ["qwen3.6-35B-balanced"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
                ["qwen3.6-27B-coder"]    = "Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            },
        };
        ModelConfigLoader.Reset();
        ModelConfigLoader.SetInstance(ModelConfigLoader.Create(config));
        return ModelConfigLoader.Instance;
    }

    [Fact]
    public void Step0_WarmSession_StayOnBoundModel()
    {
        // Session is pinned to moe-35b-solo with BoundModel set.
        // Even if prompt tokens exceed solo's window (>2048), the warm
        // affinity path must return moe-35b-solo — NOT moe-35b-pd.
        var cfg = MakeConfig();
        var loader = MakeLoader();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-session-1";
        // Bind session to solo model, register a warm slot
        ledger.Register(sid, "rtx", slotId: 0, nPast: 1000);
        ledger.UpdateBoundModel(sid, "moe-35b-solo");

        // Simulate a turn with prompt tokens > 2048 (past solo's routing window)
        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 4096, estTotalContext: 6000, requestedModel: "hydra-auto",
            verifySlot: FakeVerifyWarm);

        Assert.NotNull(result);
        Assert.Equal("moe-35b-solo", result!.Value.ModelAlias);
        Assert.Equal("warm", result!.Value.Mode);
    }

    [Fact]
    public void Step0_WarmSession_ProbeFailed_StillPinsBoundModel()
    {
        // Slot probe fails (engine down, timeout, etc.) but BoundModel
        // is set — must return cold_bound fallback on same model, never
        // flip to a different model.
        var cfg = MakeConfig();
        var loader = MakeLoader();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-session-probe-fail";
        ledger.Register(sid, "rtx", slotId: 0, nPast: 1000);
        ledger.UpdateBoundModel(sid, "moe-35b-solo");

        // Slot probe fails
        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 4096, estTotalContext: 6000, requestedModel: "hydra-auto",
            verifySlot: FakeVerifyFailed);

        Assert.NotNull(result);
        Assert.Equal("moe-35b-solo", result!.Value.ModelAlias);
        Assert.Equal("cold_bound", result!.Value.Mode);
    }

    [Fact]
    public void Step0_WarmSession_NoBoundModel_UsesCandidateWindow()
    {
        // Session has no BoundModel — auto-routing must use the
        // candidate window (prompt > 2048 → moe-35b-pd).
        var cfg = MakeConfig();
        var loader = MakeLoader();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-session-nobind";
        ledger.Register(sid, "rtx", slotId: 0, nPast: 1000);

        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 4096, estTotalContext: 6000, requestedModel: "hydra-auto");

        Assert.NotNull(result);
        Assert.Equal("moe-35b-pd", result!.Value.ModelAlias);
    }

    [Fact]
    public void ResolveEngineConfig_DecodeRole_UsesDecodeAlias()
    {
        // #481 Phase 2c: moe-35b-pd decode role must resolve to Balanced gguf (decode_alias),
        // prefill role must resolve to Mini gguf (prefill_alias).
        var loader = MakeLoader();

        var prefillConfig = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: false);
        Assert.EndsWith("Mini.gguf", prefillConfig.ModelPath);

        var decodeConfig = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: true);
        Assert.EndsWith("Balanced.gguf", decodeConfig.ModelPath);
    }

    [Fact]
    public void ResolveEngineConfig_DecodeRole_FallsBackToPrefillWhenNoDecodeAlias()
    {
        // #481 Phase 2c: When DecodeAlias is null/empty, decode role falls back to PrefillAlias.
        var loader = MakeLoader();

        var prefillConfig = loader.ResolveEngineConfig("moe-35b-solo", decodeRole: false);
        var decodeConfig = loader.ResolveEngineConfig("moe-35b-solo", decodeRole: true);
        Assert.Equal(prefillConfig.ModelPath, decodeConfig.ModelPath);
    }

    [Fact]
    public void FreshSession_DefaultEligibleFalse_SkipsDenseCombined()
    {
        // Issue #446: Fresh hydra-auto session with prompt > solo window (2048)
        // where moe-35b-pd (tier 2) and dense-27b-combined (tier 3, default_eligible=false)
        // are both feasible. Must route to moe-35b-pd, NOT dense-27b-combined.
        var cfg = MakeConfigWithCombined();
        var loader = MakeLoaderWithCombined();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        tracker.InitWorker("rtx3060", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-fresh-default-eligible";

        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 4096, estTotalContext: 6000, requestedModel: "hydra-auto");

        Assert.NotNull(result);
        Assert.Equal("moe-35b-pd", result!.Value.ModelAlias);
    }

    [Fact]
    public void WarmSession_BoundToDenseCombined_StillServed()
    {
        // Regression: warm session whose BoundModel is dense-27b-combined
        // continues to be served normally (warm affinity pins the model).
        var cfg = MakeConfigWithCombined();
        var loader = MakeLoaderWithCombined();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        tracker.InitWorker("rtx3060", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-warm-dense-combined";
        ledger.Register(sid, "rtx", slotId: 0, nPast: 1000);
        ledger.UpdateBoundModel(sid, "dense-27b-combined");

        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 1024, estTotalContext: 2000, requestedModel: "hydra-auto",
            verifySlot: FakeVerifyWarm);

        Assert.NotNull(result);
        Assert.Equal("dense-27b-combined", result!.Value.ModelAlias);
        Assert.Equal("warm", result!.Value.Mode);
    }

    [Fact]
    public void FreshSession_DefaultEligibleFalse_Moe35bPdPickedOverDenseCombined()
    {
        // Variant of the core fix test: with prompt=3000 (above solo's 2048 max),
        // moe-35b-solo is infeasible. Only moe-35b-pd (tier 2) and dense-27b-combined
        // (tier 3, default_eligible=false) are candidates. Must pick moe-35b-pd.
        var cfg = MakeConfigWithCombined();
        var loader = MakeLoaderWithCombined();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        tracker.InitWorker("rtx3060", 1);
        var health = new TestHealthMonitor();
        var ledger = new SessionLedger();
        const string sid = "test-fresh-alt-prompt";

        var result = AutoRouter.Resolve(cfg, loader, tracker, health, ledger, sid,
            promptTokens: 3000, estTotalContext: 5000, requestedModel: "hydra-auto");

        Assert.NotNull(result);
        Assert.Equal("moe-35b-pd", result!.Value.ModelAlias);
    }
}
