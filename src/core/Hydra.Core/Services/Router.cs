using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Microsoft.ML.Tokenizers;

namespace Hydra.Core.Services;

public static class Router
{
	public const int PrefillOnly = 1;
	public const int DecodeOnly = 2;
	public const int Mixed = 3;

	private static readonly TiktokenTokenizer Tokenizer =
		TiktokenTokenizer.CreateForModel("gpt-4o");

	public readonly record struct MessageSummary(
		string SessionId,
		int EstimatedTokens,
		string? PrefixHash
	);

	public static MessageSummary SummarizeMessages(
		List<Dictionary<string, object>> messages)
	{
		StringBuilder sb = new();
		int tokenCount = 0;
		string? prefixHash = null;

		foreach (var m in messages)
		{
			var role = m.GetValueOrDefault("role")?.ToString() ?? "";
			var content = m.GetValueOrDefault("content")?.ToString() ?? "";

			sb.Append(role);
			sb.Append(':');
			sb.Append(content);
			sb.Append('\n');

			tokenCount += Tokenizer.CountTokens(content);

			if (prefixHash == null && role == "system" && content.Length > 0)
			{
				prefixHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
			}
		}

		var sessionId = $"sess_{Convert.ToHexStringLower(
			SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24]}";

		return new MessageSummary(sessionId, Math.Max(1, tokenCount), prefixHash);
	}

	public static string DeriveSessionId(
		List<Dictionary<string, object>> messages)
	{
		return SummarizeMessages(messages).SessionId;
	}

	public static int EstimateRequestTokens(
		List<Dictionary<string, object>> messages,
		double charsPerToken = 4.0)
	{
		return SummarizeMessages(messages).EstimatedTokens;
	}

	public static string? ComputePrefixHash(
		List<Dictionary<string, object>> messages)
	{
		return SummarizeMessages(messages).PrefixHash;
	}

	public static WorkerConfig? PickBestPrefillWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health,
		int? maxTokens = null, string? exclude = null)
	{
		return workers
			.Where(w => w.CanPrefill && tracker.IsFree(w.Name)
				&& health.IsHealthy(w.Name)
				&& w.Name != exclude)
			.Where(w => maxTokens is null or < 0
				|| w.MaxPrefillTokens < 1
				|| maxTokens <= w.MaxPrefillTokens)
			.OrderBy(w => w.PrefillPriority)
			.FirstOrDefault();
	}

	public static WorkerConfig? PickBestDecodeWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health,
		string? exclude = null)
	{
		return workers
			.Where(w => w.CanDecode && tracker.IsFree(w.Name)
				&& health.IsHealthy(w.Name)
				&& w.Name != exclude)
			.OrderBy(w => w.DecodePriority)
			.FirstOrDefault();
	}

	public static WorkerConfig? PickBestAtomicWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health)
	{
		return workers
			.Where(w => w.CanPrefill && w.CanDecode && tracker.IsFree(w.Name) && health.IsHealthy(w.Name))
			.OrderBy(w => w.PrefillPriority)
			.FirstOrDefault()
			?? PickBestDecodeWorker(workers, tracker, health);
	}

	public static string? PrefillModel(WorkerConfig w)
	{
		return w.PrefillModelName ?? w.RouterModelName;
	}

	public static string? DecodeModel(WorkerConfig w)
	{
		return w.DecodeModelName ?? w.RouterModelName;
	}

	public static async Task<int?> PickIdleSlot(
		string llamaUrl, CancellationToken ct)
	{
		try
		{
			using var http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5)
			};
			var slots = JsonSerializer.Deserialize<List<JsonElement>>(
				await http.GetStringAsync($"{llamaUrl}/slots", ct));
			foreach (var s in slots ?? [])
				if (!s.TryGetProperty("is_processing", out var p)
					|| !p.GetBoolean())
					return s.GetProperty("id").GetInt32();
		}
		catch { }
		return null;
	}

	public static WorkerConfig? PickBestMixedWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health,
		string? exclude = null)
	{
		return workers
			.Where(w => w.WorkerType == Mixed && tracker.IsFree(w.Name)
				&& health.IsHealthy(w.Name)
				&& w.Name != exclude)
			.OrderBy(w => w.DecodePriority)
			.FirstOrDefault();
	}

	public static async Task<bool> VerifyWarmSlotAsync(
		WorkerConfig worker, SessionEntry entry, string traceId)
	{
		if (entry.SlotId == null)
			return false;

		try
		{
			using var http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5)
			};
			var url = $"{worker.LlamaUrl.TrimEnd('/')}/slots";
			var resp = await http.GetAsync(url);
			if (!resp.IsSuccessStatusCode)
				return false;

			var data = await resp.Content.ReadAsStringAsync();
			var slots = JsonSerializer.Deserialize<List<JsonElement>>(data);
			if (slots == null)
				return false;

			foreach (var slot in slots)
			{
				if (!slot.TryGetProperty("id", out var id)
					|| id.GetInt32() != entry.SlotId)
					continue;

				// Check 1: not stuck (is_processing && n_remain == 0)
				if (slot.TryGetProperty("is_processing", out var ip) && ip.GetBoolean())
				{
					var nRemain = slot.TryGetProperty("n_remain", out var nr)
						? nr.GetInt32() : 1;
					if (nRemain == 0)
						return false; // stuck
				}

				// Check 2: n_past >= entry.NPast
				var slotNPast = slot.TryGetProperty("n_past", out var sn)
					? sn.GetInt32() : 0;
				if (slotNPast < (entry.NPast > 0 ? entry.NPast : 0))
					return false;

				// Check 3: prefix_hash matches if entry has one
				if (entry.PrefixHash != null)
				{
					if (slot.TryGetProperty("prefix_hash", out var sph)
						&& sph.GetString() is { Length: > 0 } slotPrefix
						&& slotPrefix != entry.PrefixHash)
						return false;
				}

				return true;
			}
		}
		catch { }

		return false;
	}

	public static string NewTraceId()
	{
		return Guid.NewGuid().ToString("N")[..16];
	}
}
