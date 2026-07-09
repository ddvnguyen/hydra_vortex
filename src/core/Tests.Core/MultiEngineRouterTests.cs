using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;

namespace Tests.Core;

/// <summary>
/// Unit tests for the pure two-engine "work together" selection logic (no slot side-effects).
/// </summary>
public sealed class MultiEngineRouterTests
{
	private static readonly IHealthMonitorService Health = new TestHealthMonitor();

	private static (CoordinatorConfig Cfg, WorkerTracker Tracker) Build(
		bool pipeline = true, bool combined = false, int threshold = 8192,
		string policy = "pipeline",
		bool headPipelineCapable = true, bool headCombinedCapable = true,
		string modelAlias = "moe-35b-mini",
		string? peerWorker = "p100",
		bool registerAlias = true,
		string[]? overrideTensors = null)
	{
		// Phase 2a: tests register the model alias on the global ModelRegistry
		// (it's static, so concurrent tests share state; we register in the
		// helper to keep the call site simple). The "moe-35b-mini" alias is
		// already registered by the production entry; this is the
		// no-op-default. Pass registerAlias: false to exercise the
		// unresolvable-alias path (Resolve() throws → Select() skips the head).
		if (registerAlias)
		{
			ModelRegistry.RegisterForTest(new EngineConfig(
				ModelAlias: modelAlias,
				ModelPath: "/models/test-" + modelAlias + ".gguf",
				OverrideTensors: overrideTensors ?? new[] { "ffn_.*_exps=PEER" }));
		}
		var cfg = new CoordinatorConfig
		{
			UseLlamaEngine = true,
			PipelineEnabled = pipeline,
			CombinedEnabled = combined,
			MultiEngineThreshold = threshold,
			MultiEnginePolicy = policy,
			Workers = new List<WorkerConfig>
			{
				new()
				{
					Name = "rtx", Host = "localhost", RpcPort = 9601,
					LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1,
					Role = "head", PeerWorker = peerWorker,
					PeerHost = "192.168.122.21", PeerPort = 9700,
					PipelineCapable = headPipelineCapable, CombinedCapable = headCombinedCapable,
					ModelAlias = modelAlias
				},
				new()
				{
					Name = "p100", Host = "localhost", RpcPort = 9602,
					LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1,
					Role = "worker"
				}
			}
		};
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		return (cfg, tracker);
	}

	[Fact]
	public void Selects_Pipeline_For_Large_Request()
	{
		var (cfg, tracker) = Build();
		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000);
		Assert.NotNull(plan);
		Assert.Equal(MultiEngineMode.Pipeline, plan!.Value.Mode);
		Assert.Equal("rtx", plan.Value.Head.Name);
		Assert.Equal("p100", plan.Value.Peer.Name);
		Assert.Equal("moe-35b-mini", plan.Value.EngineConfig.ModelAlias);
	}

	[Fact]
	public void Skips_When_Below_Threshold()
	{
		var (cfg, tracker) = Build(threshold: 8192);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 100));
	}

	[Fact]
	public void Skips_When_Not_Engine_Mode()
	{
		var (cfg, tracker) = Build();
		var solo = cfg with { UseLlamaEngine = false };
		Assert.Null(MultiEngineRouter.Select(solo, solo.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Skips_When_Both_Modes_Disabled()
	{
		var (cfg, tracker) = Build(pipeline: false, combined: false);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Skips_When_Head_Busy()
	{
		var (cfg, tracker) = Build();
		Assert.True(tracker.TryAcquireSlot("rtx", out _, "decode"));
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Skips_When_Peer_Busy()
	{
		var (cfg, tracker) = Build();
		Assert.True(tracker.TryAcquireSlot("p100", out _, "decode"));
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Policy_Prefers_Combined_When_Configured()
	{
		var (cfg, tracker) = Build(pipeline: true, combined: true, policy: "combined");
		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000);
		Assert.Equal(MultiEngineMode.Combined, plan!.Value.Mode);
		Assert.Equal("moe-35b-mini", plan.Value.EngineConfig.ModelAlias);
	}

	[Fact]
	public void Falls_To_Other_Mode_When_Preferred_Not_Usable()
	{
		// Prefer pipeline, but head lacks pipeline capability → combined is selected.
		var (cfg, tracker) = Build(pipeline: true, combined: true, policy: "pipeline",
			headPipelineCapable: false);
		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000);
		Assert.Equal(MultiEngineMode.Combined, plan!.Value.Mode);
	}

	[Fact]
	public void Skips_When_Model_Alias_Not_Registered()
	{
		// Phase 2a: the old "no split configured" gate is gone. The router
		// now skips a head whose ModelAlias doesn't resolve in the
		// ModelRegistry. registerAlias: false leaves this alias unregistered,
		// so ModelRegistry.Resolve throws and Select skips the head.
		var (cfg, tracker) = Build(modelAlias: "definitely-not-registered-9c2", combined: true, registerAlias: false);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Skips_Pipeline_When_No_Override_Tensor_Configured()
	{
		// The resolved EngineConfig can lack OverrideTensors (e.g. a layer-split
		// DENSE-profile alias like "dense-27b-q5" has none). PIPELINE mode needs
		// a runtime override-tensor for the engine to route anything to the peer;
		// without one, ModeUsable must refuse the plan rather than let it through
		// and silently degrade to solo after the peer is already reserved.
		var (cfg, tracker) = Build(pipeline: true, combined: false, overrideTensors: Array.Empty<string>());
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Falls_Back_To_Combined_When_Pipeline_Has_No_Override_Tensor()
	{
		var (cfg, tracker) = Build(pipeline: true, combined: true, policy: "pipeline",
			overrideTensors: Array.Empty<string>());
		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000);
		Assert.Equal(MultiEngineMode.Combined, plan!.Value.Mode);
	}

	[Fact]
	public void Skips_When_Head_Has_No_Peer()
	{
		var (cfg, tracker) = Build(peerWorker: null);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	// ── #383 T5 (now: peer-only with slots=0) ──

	[Fact]
	public void Selects_CombinedStatic_Peer_With_Zero_Slots()
	{
		// Peer-only workers (slots=0) are dedicated to a head and never
		// "free" in the tracker (IsFree requires a free slot). The router
		// must skip the IsFree check for them.
		ModelRegistry.RegisterForTest(new EngineConfig(
			ModelAlias: "dense-27b-q5",
			ModelPath: "/models/test-dense-27b-q5.gguf",
			NGpuLayers: 65, NCtx: 96000,
			SplitMode: "layer", TensorSplit: new[] { 25.0, 40.0 }));
		var cfg = new CoordinatorConfig
		{
			UseLlamaEngine = true,
			CombinedEnabled = true,
			MultiEngineThreshold = 0,
			MultiEnginePolicy = "combined",
			Workers = new List<WorkerConfig>
			{
				new()
				{
					Name = "rtx", Host = "localhost", RpcPort = 9601,
					LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1,
					Role = "head", PeerWorker = "rtx3060",
					CombinedCapable = true, ModelAlias = "dense-27b-q5"
				},
				new()
				{
					Name = "rtx3060", Host = "localhost", RpcPort = 9603,
					LlamaUrl = "http://localhost:8081", WorkerType = 3, Slots = 0
				}
			}
		};
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);

		// Peer has 0 slots → IsFree returns false, but Select must still find it.
		Assert.False(tracker.IsFree("rtx3060"),
			"0-slot peer must not be IsFree (it has no free slots)");

		// Peer must still be exclusively reservable (all-free check: 0 == 0)
		Assert.True(tracker.TryReserveWorkerExclusive("rtx3060"),
			"0-slot peer must be exclusively reservable");

		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 100);
		Assert.NotNull(plan);
		Assert.Equal(MultiEngineMode.Combined, plan!.Value.Mode);
		Assert.Equal("rtx", plan.Value.Head.Name);
		Assert.Equal("rtx3060", plan.Value.Peer.Name);
		Assert.Equal("dense-27b-q5", plan.Value.EngineConfig.ModelAlias);
	}

	// ── P3.0 (#366): peer must be exclusively reservable for COMBINED admission ──

	[Fact]
	public void Skips_When_Peer_Exclusive_Reserved()
	{
		// P3.0: an exclusively-reserved peer is invisible to the router — no
		// concurrent SOLO will be routed to it while COMBINED is driving the head.
		var (cfg, tracker) = Build();
		Assert.True(tracker.TryReserveWorkerExclusive("p100"));
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void MultiSlot_Peer_Some_Slots_Free_Still_Skipped_For_Exclusive_Reservation()
	{
		// P3.0 admission requires the peer to be FULLY idle, not just have at
		// least one free slot. The router's IsFree() check (any-free) is the
		// first gate; the scheduler's TryReserveWorkerExclusive (all-free) is
		// the second. Here we exercise the all-free gate.
		var cfg = new CoordinatorConfig
		{
			UseLlamaEngine = true,
			PipelineEnabled = true,
			CombinedEnabled = false,
			MultiEngineThreshold = 8192,
			MultiEnginePolicy = "pipeline",
			Workers = new List<WorkerConfig>
			{
				new()
				{
					Name = "rtx", Host = "localhost", RpcPort = 9601,
					LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1,
					Role = "head", PeerWorker = "p100",
					PipelineCapable = true, ModelAlias = "moe-35b-mini"
				},
				new()
				{
					Name = "p100", Host = "localhost", RpcPort = 9602,
					LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 2,
					Role = "worker"
				}
			}
		};
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		// Rent 1 of 2 peer slots (so p100 is partial-free, not full-free)
		Assert.True(tracker.TryAcquireSlot("p100", out _, "decode"));
		// Router may still see p100 as "free" (any-free) — that's the first gate.
		var plan = MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000);
		// But the scheduler's second gate (all-free) will reject. We simulate
		// that here by checking the tracker API:
		Assert.False(tracker.TryReserveWorkerExclusive("p100"),
			"Multi-slot peer with one busy slot must NOT be reservable for COMBINED");
		_ = plan; // router may have picked the plan; the scheduler gate is what matters
	}
}
