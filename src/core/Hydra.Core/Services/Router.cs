using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Microsoft.ML.Tokenizers;
using Serilog;

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
		string? PrefixHash,
		int SystemPromptTokens
	);

	public static MessageSummary SummarizeMessages(
		List<Dictionary<string, object>> messages)
	{
		var sw = Stopwatch.StartNew();
		StringBuilder sb = new();
		int tokenCount = 0;
		string? prefixHash = null;
		int systemPromptTokens = 0;

		for (int i = 0; i < messages.Count; i++)
		{
			var m = messages[i];
			var role = m.GetValueOrDefault("role")?.ToString() ?? "";
			var content = m.GetValueOrDefault("content")?.ToString() ?? "";

			sb.Append(role);
			sb.Append(':');
			sb.Append(content);
			sb.Append('\n');

			var msgSw = Stopwatch.StartNew();
			var tokens = Tokenizer.CountTokens(content);
			msgSw.Stop();
			if (msgSw.ElapsedMilliseconds > 200)
			{
				Log.Warning("event=summarize_slow_tokenize index={Index} role={Role} content_chars={ContentChars} elapsed_ms={ElapsedMs}",
					i, role, content.Length, msgSw.ElapsedMilliseconds);
			}
			tokenCount += tokens;

			if (role == "system" && content.Length > 0)
			{
				systemPromptTokens += tokens;
				if (prefixHash == null)
				{
					prefixHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
				}
			}
		}

		var sessionId = $"sess_{Convert.ToHexStringLower(
			SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24]}";

		if (sw.ElapsedMilliseconds > 200)
		{
			Log.Warning("event=summarize_messages_slow elapsed_ms={ElapsedMs} message_count={MessageCount} total_tokens={TotalTokens}",
				sw.ElapsedMilliseconds, messages.Count, tokenCount);
		}

		return new MessageSummary(sessionId, Math.Max(1, tokenCount), prefixHash, systemPromptTokens);
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

	[Obsolete("Use AutoRouter.Resolve instead")]
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

	[Obsolete("Use AutoRouter.Resolve instead")]
	public static WorkerConfig? PickBestDecodeWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health,
		string? exclude = null,
		List<string>? allowedModels = null)
	{
		return workers
			.Where(w => w.CanDecode && tracker.IsFree(w.Name)
				&& health.IsHealthy(w.Name)
				&& w.Name != exclude
				&& IsModelAllowed(health, w.Name, allowedModels))
			.OrderBy(w => w.DecodePriority)
			.FirstOrDefault();
	}

	[Obsolete("Use AutoRouter.Resolve instead")]
	public static WorkerConfig? PickBestAtomicWorker(
		List<WorkerConfig> workers, IWorkerTracker tracker,
		IHealthMonitorService health,
		List<string>? allowedModels = null)
	{
		return workers
			.Where(w => w.CanPrefill && w.CanDecode && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)
				&& IsModelAllowed(health, w.Name, allowedModels))
			.OrderBy(w => w.PrefillPriority)
			.FirstOrDefault()
			?? PickBestDecodeWorker(workers, tracker, health, allowedModels: allowedModels);
	}

	public static string? PrefillModel(WorkerConfig w)
	{
		return w.PrefillModelName ?? w.RouterModelName;
	}

	public static string? DecodeModel(WorkerConfig w)
	{
		return w.DecodeModelName ?? w.RouterModelName;
	}

	/// <summary>
	/// Returns true when the worker's loaded model is compatible with the
	/// request. When <paramref name="allowedModels"/> is empty, all workers
	/// pass. Otherwise the worker must advertise or currently host one of the
	/// allowed model identities. Match is by GGUF-file alias (engine preset) or
	/// by GGUF file name substring (#479/S3: the /v1/models CurrentModel poll is
	/// removed; residency is now the engine's own report).
	/// </summary>
	public static bool IsModelAllowed(IHealthMonitorService health, string nodeName, List<string>? allowedModels)
	{
		if (allowedModels == null || allowedModels.Count == 0) return true;
		var nodeInfo = health.GetNodeInfo(nodeName);
		if (nodeInfo == null) return true; // no info → allow (back-compat)

		// A worker passes if any allowed identity is one of its advertised
		// preset aliases (can-host) or is its current resident model.
		var resident = nodeInfo.CurrentModel;
		foreach (var allowed in allowedModels)
		{
			if (nodeInfo.PresetAliases.Count > 0
				&& nodeInfo.PresetAliases.Any(pa => pa.Contains(allowed, StringComparison.OrdinalIgnoreCase)
					|| allowed.Contains(pa, StringComparison.OrdinalIgnoreCase)))
				return true;
			if (!string.IsNullOrEmpty(resident)
				&& (resident.Contains(allowed, StringComparison.OrdinalIgnoreCase)
					|| allowed.Contains(resident, StringComparison.OrdinalIgnoreCase)))
				return true;
		}
		return false;
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

	[Obsolete("Use AutoRouter.Resolve instead")]
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
