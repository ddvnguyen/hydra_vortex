using System.Text.Json;

namespace Hydra.Core.Models;

/// <summary>
/// Worker node configuration — env-var + JSON driven.
/// </summary>
public sealed record WorkerConfig
{
	public string Name { get; init; } = "";
	public string Host { get; init; } = "";
	public int RpcPort { get; init; }
	public int LlamaRpcPort { get; init; }
	public string LlamaUrl { get; init; } = "";
	public int WorkerType { get; init; } = 3;
	public int Slots { get; init; } = 1;
	public int PrefillPriority { get; init; } = 1;
	public int DecodePriority { get; init; } = 1;
	public float DecodeSpeedTps { get; init; } = 30f;
	public int MaxPrefillTokens { get; init; } = -1;
	public string? RouterModelName { get; init; }
	public string? PrefillModelName { get; init; }
	public string? DecodeModelName { get; init; }

	public bool CanPrefill => (WorkerType & 1) != 0;
	public bool CanDecode => (WorkerType & 2) != 0;
}

/// <summary>
/// Coordinator configuration — all values from HYDRA_COORD_* env vars.
/// </summary>
public sealed record CoordinatorConfig
{
	public string Host { get; init; } = Env("HYDRA_COORD_HOST", "0.0.0.0");
	public int Port { get; init; } = EnvInt("HYDRA_COORD_PORT", 9000);
	public string StoreHost { get; init; } = Env("HYDRA_COORD_STORE_HOST", "127.0.0.1");
	public int StorePort { get; init; } = EnvInt("HYDRA_COORD_STORE_PORT", 9500);
	public int HealthPollIntervalS { get; init; } = EnvInt("HYDRA_COORD_HEALTH_POLL_INTERVAL_S", 20);
	public int HealthMaxFailures { get; init; } = EnvInt("HYDRA_COORD_HEALTH_MAX_FAILURES", 3);
	public float CharsPerToken { get; init; } = float.Parse(Env("HYDRA_COORD_CHARS_PER_TOKEN", "4.0"));
	public int LlamaRequestTimeoutS { get; init; } = EnvInt("HYDRA_COORD_LLAMA_REQUEST_TIMEOUT_S", 1800);
	public int SessionIdleTimeoutS { get; init; } = EnvInt("HYDRA_COORD_SESSION_IDLE_TIMEOUT_S", 3600);
	// Cold/warm routing is gated on the *new prompt* token count (output is ignored):
	//   newPrompt <= AtomicThreshold → single-worker atomic route (no P/D split)
	//   newPrompt <= WarmThreshold   → reuse the warm affinity slot for follow-up turns
	// AtomicThreshold replaces the former AtomicTokenThreshold + SmallRequestBypassThreshold.
	// Back-compat: the legacy HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD env var is honoured as a fallback.
	public int AtomicThreshold { get; init; } =
		EnvInt("HYDRA_COORD_ATOMIC_THRESHOLD", EnvInt("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", 2048));
	public int WarmThreshold { get; init; } = EnvInt("HYDRA_COORD_WARM_THRESHOLD", 5120);
	public double NPastGuardThreshold { get; init; } = double.Parse(Env("HYDRA_COORD_N_PAST_GUARD_THRESHOLD", "0.6"));
	public int WorkerErrorThreshold { get; init; } = EnvInt("HYDRA_COORD_WORKER_ERROR_THRESHOLD", 3);
	public string RunMode { get; init; } = Env("HYDRA_COORD_RUN_MODE", "concurrency");
	public bool MixPrecisionEnabled { get; init; } = EnvBool("HYDRA_COORD_MIX_PRECISION_ENABLED", false);
	public bool RawSlot { get; init; } = EnvBool("HYDRA_COORD_RAW_SLOT", false);
	public bool PrefixCheckpointEnabled { get; init; } = EnvBool("HYDRA_COORD_PREFIX_CHECKPOINT_ENABLED", true);
	public bool WarmSlotVerificationEnabled { get; init; } = EnvBool("HYDRA_COORD_WARM_SLOT_VERIFY", true);
	public string PrefixCheckpointName { get; init; } = Env("HYDRA_COORD_PREFIX_CHECKPOINT_NAME", "system_prompt");
	public bool DevModeEnabled { get; init; } = EnvBool("HYDRA_DEV_MODE", false);
	public List<WorkerConfig> Workers { get; set; } = [];

	public static List<WorkerConfig> LoadWorkers()
	{
		// Canonical: load from a JSON file (compose deploys use this).
		var file = Environment.GetEnvironmentVariable("HYDRA_COORD_CONFIG_FILE");
		if (!string.IsNullOrWhiteSpace(file))
		{
			if (!File.Exists(file))
				throw new InvalidOperationException(
					$"HYDRA_COORD_CONFIG_FILE={file} does not exist");
			try
			{
				return JsonSerializer.Deserialize<List<WorkerConfig>>(File.ReadAllText(file),
					new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true,
						PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
					}) ?? [];
			}
			catch (JsonException ex)
			{
				throw new InvalidOperationException(
					$"Failed to parse worker config at {file}: {ex.Message}", ex);
			}
		}

		// Legacy: inline JSON env (kept for unit tests and ad-hoc local runs).
		// If both are set, the file path wins — but warn so it's not silent.
		var json = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS");
		if (!string.IsNullOrWhiteSpace(json))
			return JsonSerializer.Deserialize<List<WorkerConfig>>(json,
				 new JsonSerializerOptions
				 {
					 PropertyNameCaseInsensitive = true,
					 PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
				 }) ?? [];

		// Fallback default worker for testing
		return new List<WorkerConfig>
		  {
				new()
				{
					Name = "rtx",
					Host = "localhost",
					RpcPort = 9601,
					LlamaUrl = "http://localhost:8080",
					WorkerType = 3,
					Slots = 2,
					PrefillPriority = 1,
					DecodePriority = 2,
					PrefillModelName = "nano",
					DecodeModelName = "balanced"
				},
				new()
				{
					Name = "p100",
					Host = "localhost",
					RpcPort = 9602,
					LlamaUrl = "http://192.168.122.21:8086",
					WorkerType = 2,
					Slots = 1,
					PrefillPriority = 100,
					DecodePriority = 1
				}
		  };
	}

	public void Validate()
	{
		if (Workers.Count == 0) throw new InvalidOperationException("No workers configured");
		foreach (var w in Workers)
		{
			if (string.IsNullOrWhiteSpace(w.Name)) throw new InvalidOperationException("Worker name required");
			if (string.IsNullOrWhiteSpace(w.Host)) throw new InvalidOperationException($"Worker '{w.Name}' host required");
			if (w.RpcPort <= 0) throw new InvalidOperationException($"Worker '{w.Name}' rpc_port required");
			if (!Uri.TryCreate(w.LlamaUrl, UriKind.Absolute, out _)) throw new InvalidOperationException($"Worker '{w.Name}' llama_url invalid");
		}
	}

	private static string Env(string k, string fb) => Environment.GetEnvironmentVariable(k) ?? fb;
	private static int EnvInt(string k, int fb) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : fb;
	private static bool EnvBool(string k, bool fb) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : fb;
}

/// <summary>
/// Session routing entry — which node, which slot, KV context size.
/// </summary>
public sealed class SessionEntry
{
	public string SessionId { get; set; } = "";
	public string NodeName { get; set; } = "";
	public int? SlotId { get; set; }
	public int NPast { get; set; }
	public bool HasStoreState { get; set; }
	public bool SlotFreed { get; set; }
	public string? PrefixHash { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cached health snapshot for one worker node.
/// </summary>
public sealed class NodeInfo
{
	public string NodeName { get; set; } = "";
	public bool Healthy { get; set; }
	public int SlotsTotal { get; set; }
	public int SlotsIdle { get; set; }
	public int ConsecutiveFailures { get; set; }
	public DateTime LastCheck { get; set; }
	public int StuckSlots { get; set; }
	public List<SlotInfo> Slots { get; set; } = [];
	/// <summary>Alias of the model currently loaded on this node (from llama /v1/models). Empty = unknown.</summary>
	public string CurrentModel { get; set; } = "";
}

public sealed class SlotInfo
{
	public int Id { get; set; }
	public bool IsProcessing { get; set; }
	public int NPast { get; set; }
	public DateTime LastActive { get; set; } = DateTime.UtcNow;
	public int StuckPollCount { get; set; }
}
