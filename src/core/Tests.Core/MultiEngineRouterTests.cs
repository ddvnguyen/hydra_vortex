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
		string? pipelineSplit = "blk\\.(2[0-9]|3[0-9])\\..*=PEER",
		string? combinedSplit = "ffn_.*_exps=PEER",
		string? peerWorker = "p100")
	{
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
					PipelineOtSplit = pipelineSplit, CombinedOtSplit = combinedSplit
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
		Assert.Contains("PEER", plan.Value.OtSplit);
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
		Assert.Equal("ffn_.*_exps=PEER", plan.Value.OtSplit);
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
	public void Skips_When_No_Split_Configured()
	{
		var (cfg, tracker) = Build(pipelineSplit: null, combinedSplit: null, combined: true);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
	}

	[Fact]
	public void Skips_When_Head_Has_No_Peer()
	{
		var (cfg, tracker) = Build(peerWorker: null);
		Assert.Null(MultiEngineRouter.Select(cfg, cfg.Workers, tracker, Health, estTokens: 20000));
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
					PipelineCapable = true, PipelineOtSplit = "blk\\..*=PEER"
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
