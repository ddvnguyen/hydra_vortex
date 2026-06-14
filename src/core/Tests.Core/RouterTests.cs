using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;

namespace Tests.Core;

/// <summary>
/// Test health monitor that always reports healthy.
/// </summary>
internal sealed class TestHealthMonitor : IHealthMonitorService
{
	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	public bool IsHealthy(string nodeName) => true;
	public bool IsStoreHealthy => true;
	public int? GetIdleSlot(string nodeName) => null;
	public Dictionary<string, object> GetHealthSummary() => new();
}

public sealed class RouterTests
{
	private static readonly IHealthMonitorService Health = new TestHealthMonitor();

	[Fact]
	public void Derive_Session_Id_Is_Consistent()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var id1 = Router.DeriveSessionId(msgs);
		var id2 = Router.DeriveSessionId(msgs);
		Assert.Equal(id1, id2);
		Assert.StartsWith("sess_", id1);
		Assert.Equal(24 + 5, id1.Length); // "sess_" + 24 hex
	}

	[Fact]
	public void Derive_Session_Id_Differs_For_Different_Content()
	{
		var msgs1 = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hello" } };
		var msgs2 = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "world" } };
		Assert.NotEqual(Router.DeriveSessionId(msgs1), Router.DeriveSessionId(msgs2));
	}

	[Fact]
	public void Estimate_Tokens_Uses_Char_Ratio()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = "hello world" } // 11 chars
		};
		var est = Router.EstimateRequestTokens(msgs, 4.0);
		Assert.Equal(11 / 4, est); // 11/4 = 2
	}

	[Fact]
	public void Compute_Prefix_Hash_With_System_Message()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are helpful." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var hash = Router.ComputePrefixHash(msgs);
		Assert.NotNull(hash);
		Assert.Equal(16, hash!.Length);
	}

	[Fact]
	public void Compute_Prefix_Hash_No_System_Returns_Null()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		Assert.Null(Router.ComputePrefixHash(msgs));
	}

	[Fact]
	public void Compute_Prefix_Hash_Same_System_Same_Hash()
	{
		var msgs1 = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are a helpful assistant." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var msgs2 = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are a helpful assistant." },
			new() { ["role"] = "user", ["content"] = "different question" }
		};
		var hash1 = Router.ComputePrefixHash(msgs1);
		var hash2 = Router.ComputePrefixHash(msgs2);
		Assert.NotNull(hash1);
		Assert.Equal(hash1, hash2);
	}

	[Fact]
	public void Compute_Prefix_Hash_Different_System_Different_Hash()
	{
		var msgs1 = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are a helpful assistant." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var msgs2 = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are a coding expert." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var hash1 = Router.ComputePrefixHash(msgs1);
		var hash2 = Router.ComputePrefixHash(msgs2);
		Assert.NotEqual(hash1, hash2);
	}

	[Fact]
	public void Compute_Prefix_Hash_Only_First_System_Used()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "First system prompt." },
			new() { ["role"] = "system", ["content"] = "Second system prompt." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var hash = Router.ComputePrefixHash(msgs);
		var expected = Router.ComputePrefixHash(new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "First system prompt." },
			new() { ["role"] = "user", ["content"] = "hello" }
		});
		Assert.Equal(expected, hash);
	}

	[Fact]
	public void Prefix_Key_Format_Is_Correct()
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "system", ["content"] = "You are helpful." },
			new() { ["role"] = "user", ["content"] = "hello" }
		};
		var hash = Router.ComputePrefixHash(msgs);
		var prefixKey = $"prefix/{hash}.kv";
		Assert.StartsWith("prefix/", prefixKey);
		Assert.EndsWith(".kv", prefixKey);
		Assert.Equal($"prefix/{hash}.kv", prefixKey);
	}

	[Fact]
	public void Pick_Best_Prefill_Worker_Respects_Priority()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestPrefillWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("rtx", picked!.Name);
	}

	[Fact]
	public void Pick_Best_Prefill_Worker_Skips_DecodeOnly()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "p100", WorkerType = 2, PrefillPriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		Assert.Null(Router.PickBestPrefillWorker(workers, tracker, Health));
	}

	[Fact]
	public void Pick_Best_Decode_Worker_Respects_Priority()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, DecodePriority = 2 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestDecodeWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("p100", picked!.Name);
	}

	[Fact]
	public void Pick_Best_Decode_Worker_Excludes()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, DecodePriority = 1 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 2 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestDecodeWorker(workers, tracker, Health, exclude: "rtx");
		Assert.NotNull(picked);
		Assert.Equal("p100", picked!.Name);
	}

	[Fact]
	public void Pick_Best_Prefill_Worker_Respects_Max_Tokens()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1, MaxPrefillTokens = 10000 },
			new() { Name = "p100", WorkerType = 2, PrefillPriority = 2 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestPrefillWorker(workers, tracker, Health, maxTokens: 20000);
		Assert.Null(picked); // RTX capped at 10000, P100 can't prefill
	}

	[Fact]
	public void Pick_Best_Decode_Worker_Skips_Busy()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, DecodePriority = 1 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 2 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);
		tracker.Acquire("rtx", "decode");

		var picked = Router.PickBestDecodeWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("p100", picked!.Name);
	}

	[Fact]
	public void Pick_All_Busy_Returns_Null()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);
		tracker.Acquire("rtx", "prefill");

		Assert.Null(Router.PickBestPrefillWorker(workers, tracker, Health));
	}

	[Fact]
	public void Prefill_Model_Falls_Back_To_Router()
	{
		var w = new WorkerConfig { RouterModelName = "nano" };
		Assert.Equal("nano", Router.PrefillModel(w));
	}

	[Fact]
	public void Prefill_Model_Prefers_Prefill_Field()
	{
		var w = new WorkerConfig { PrefillModelName = "mini", RouterModelName = "nano" };
		Assert.Equal("mini", Router.PrefillModel(w));
	}

	[Fact]
	public void Decode_Model_Falls_Back_To_Router()
	{
		var w = new WorkerConfig { RouterModelName = "balanced" };
		Assert.Equal("balanced", Router.DecodeModel(w));
	}

	[Fact]
	public void Mixed_Worker_Selection()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1, DecodePriority = 2 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestMixedWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("rtx", picked!.Name); // P100 can't prefill
	}

	[Fact]
	public void PickBestAtomicWorker_Prefers_Mixed_Over_DecodeOnly()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1, DecodePriority = 2 },
			new() { Name = "p100", WorkerType = 2, DecodePriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestAtomicWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("rtx", picked!.Name);
	}

	[Fact]
	public void PickBestAtomicWorker_Falls_Back_To_DecodeOnly()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "p100", WorkerType = 2, DecodePriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		var picked = Router.PickBestAtomicWorker(workers, tracker, Health);
		Assert.NotNull(picked);
		Assert.Equal("p100", picked!.Name);
	}

	[Fact]
	public void PickBestAtomicWorker_Returns_Null_When_All_Busy()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", WorkerType = 3, PrefillPriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);
		tracker.Acquire("rtx", "decode");

		Assert.Null(Router.PickBestAtomicWorker(workers, tracker, Health));
	}

	[Fact]
	public void PickBestAtomicWorker_Skips_PrefillOnly()
	{
		var workers = new List<WorkerConfig>
		{
			new() { Name = "prefill_only", WorkerType = 1, PrefillPriority = 1 },
		};
		var tracker = new WorkerTracker();
		foreach (var w in workers) tracker.InitWorker(w.Name);

		Assert.Null(Router.PickBestAtomicWorker(workers, tracker, Health));
	}
}
