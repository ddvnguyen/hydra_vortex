using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Tests.Core;

namespace Tests.Core.Services;

public class AutoRouterTests
{
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
                PrefillModelFileName = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                DecodeModelFileName = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
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
                PrefillModelFileName = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                DecodeModelFileName = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
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
            SchemaVersion = 2,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = models,
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
            promptTokens: 4096, estTotalContext: 6000, requestedModel: "hydra-auto");

        Assert.NotNull(result);
        Assert.Equal("moe-35b-solo", result!.Value.ModelAlias);
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
    public void ResolveEngineConfig_DecodeRole_UsesDecodeModelFileName()
    {
        // moe-35b-pd decode role must resolve to Balanced gguf (decode_model_file_name),
        // prefill role must resolve to Mini gguf (prefill_model_file_name).
        var loader = MakeLoader();

        var prefillConfig = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: false);
        Assert.EndsWith("Mini.gguf", prefillConfig.ModelPath);

        var decodeConfig = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: true);
        Assert.EndsWith("Balanced.gguf", decodeConfig.ModelPath);
    }

    [Fact]
    public void ResolveEngineConfig_DecodeRole_FallsBackToPrefillWhenNoDecodeFile()
    {
        // When DecodeModelFileName is null/empty, decode role falls back to PrefillModelFileName.
        var loader = MakeLoader();

        var prefillConfig = loader.ResolveEngineConfig("moe-35b-solo", decodeRole: false);
        var decodeConfig = loader.ResolveEngineConfig("moe-35b-solo", decodeRole: true);
        Assert.Equal(prefillConfig.ModelPath, decodeConfig.ModelPath);
    }
}
