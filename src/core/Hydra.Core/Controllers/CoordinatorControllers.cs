using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;

namespace Hydra.Core.Controllers;

[ApiController]
public class ModelsController : ControllerBase
{
	[HttpGet("/v1/models")]
	public IActionResult Models()
	{
		return new JsonResult(new
		{
			@object = "list",
			data = new[]
			{
				new { id = "balanced", @object = "model", owned_by = "hydra" },
			}
		});
	}
}

[ApiController]
public class CompletionsController : ControllerBase
{
	static JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
	private readonly IWorkerScheduler _scheduler;

	public CompletionsController(IWorkerScheduler scheduler) => _scheduler = scheduler;

	[HttpPost("/v1/chat/completions")]
	public async Task<IActionResult> ChatCompletions(CancellationToken ct)
	{
		Dictionary<string, object>? body;
		try
		{
			var contentLength = Request.ContentLength ?? 0;
			if (contentLength is <= 0 or > 1_048_576) // max 1 MiB
			{
				return contentLength <= 0
					? BadRequest(new { error = "Empty request body" })
					: BadRequest(new { error = "Request body too large" });
			}

			// Read exactly Content-Length bytes — ReadToEndAsync hangs on keep-alive
			var buf = new byte[contentLength];
			var offset = 0;
			while (offset < contentLength)
			{
				var n = await Request.Body.ReadAsync(buf, offset, (int)(contentLength - offset), ct);
				if (n == 0) break;
				offset += n;
			}
			var json = Encoding.UTF8.GetString(buf, 0, offset);
			body = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOpts);
		}
		catch
		{
			return BadRequest(new { error = "Invalid JSON" });
		}

		if (body is null || !body.TryGetValue("messages", out var msgObj) || msgObj is not JsonElement msgsEl)
		{
			return UnprocessableEntity(new { error = "messages is required" });
		}

		var messages = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(msgsEl.GetRawText(), _jsonOpts) ?? [];

		int maxTokens = body.TryGetValue("max_tokens", out var mt)
			&& mt is JsonElement mte ? mte.GetInt32() : 1024;
		// Ensure max_tokens is in the body so llama-server gets it (OpenWebUI omits it)
		body["max_tokens"] = maxTokens;
		// Session ID resolution priority:
		//   1. X-Session-Id HTTP header (Python Coordinator compat)
		//   2. x-opencode-session HTTP header (opencode proxy injection)
		//   3. x-conversation-id HTTP header (generic conversation tracking)
		//   4. session_id from JSON body
		//   5. Auto-derived from message content (SHA256 fallback)
		string? sessionId = null;
		if (Request.Headers.TryGetValue("X-Session-Id", out var xsHdr))
			sessionId = xsHdr.FirstOrDefault();
		if (sessionId == null && Request.Headers.TryGetValue("x-opencode-session", out var osHdr))
			sessionId = osHdr.FirstOrDefault();
		if (sessionId == null && Request.Headers.TryGetValue("x-conversation-id", out var ciHdr))
			sessionId = ciHdr.FirstOrDefault();
		if (sessionId == null && body.TryGetValue("session_id", out var sid)
			&& sid is JsonElement side)
			sessionId = side.GetString();

		// Strip internal fields before forwarding to llama-server
		body.Remove("session_id");

		// Single pass: derive session ID, token estimate, and prefix hash
		var summary = Router.SummarizeMessages(messages);
		sessionId ??= summary.SessionId;

		try
		{
			var result = await _scheduler.SubmitAsync(body, messages, sessionId, summary.EstimatedTokens, maxTokens, summary.PrefixHash, ct);
			if (result is IAsyncEnumerable<byte[]> stream)
			{
				Response.ContentType = "text/event-stream";
				Response.Headers["X-Hydra-Node"] = _scheduler.LastDispatchedNode ?? "unknown";
				// M-Perf.9 #289: surface the model identity that served this
				// response. The hash is truncated to 12 hex chars (48 bits =
				// 6 bytes of entropy, enough to disambiguate a few hundred
				// GGUFs in practice) to keep the header short for log
				// readability. Full 64-char hash is in the X-Hydra-Model
				// trailer (see below) for debugging.
				var model = _scheduler.LastDispatchedModel;
				var modelHash = _scheduler.LastDispatchedModelHash;
				if (!string.IsNullOrEmpty(model))
					Response.Headers["X-Hydra-Model"] = model;
				if (!string.IsNullOrEmpty(modelHash))
					Response.Headers["X-Hydra-Model-Hash"] = modelHash.Length > 12 ? modelHash[..12] : modelHash;
				await foreach (var chunk in stream.WithCancellation(ct))
				{
					await Response.Body.WriteAsync(chunk, ct);
				}
				return new EmptyResult();
			}

			// Non-streaming path: headers must be set on the JsonResult
			// controller context, not on the response body. We attach the
			// same model identity headers here.
			Response.Headers["X-Hydra-Node"] = _scheduler.LastDispatchedNode ?? "unknown";
			var modelNs = _scheduler.LastDispatchedModel;
			var modelHashNs = _scheduler.LastDispatchedModelHash;
			if (!string.IsNullOrEmpty(modelNs))
				Response.Headers["X-Hydra-Model"] = modelNs;
			if (!string.IsNullOrEmpty(modelHashNs))
				Response.Headers["X-Hydra-Model-Hash"] = modelHashNs.Length > 12 ? modelHashNs[..12] : modelHashNs;
			return new JsonResult(result);
		}
		catch (OperationCanceledException)
		{
			return StatusCode(499);
		}
		catch (Exception ex)
		{
			return StatusCode(503, new { error = ex.Message });
		}
		finally
		{
			// Fire-and-forget on purpose: the SSE stream is already done (or
			// the client cancelled) — the HTTP response should not wait for the
			// round-trip to StateGet + Store Put. The race that #277 fixed
			// is still closed because NotifyStreamComplete awaits the bg-save
			// internally *before* releasing the slot lease, so the slot is not
			// returned to the pool while a save is in flight. Disposal of the
			// lease is wrapped in try/catch inside NotifyStreamComplete so an
			// exception becomes a log line rather than an unobserved task.
			_ = _scheduler.NotifyStreamComplete(sessionId);
		}
	}
}

[ApiController]
public class HealthController : ControllerBase
{
	private readonly CoordinatorConfig _cfg;
	private readonly ISessionLedger _ledger;
	private readonly IWorkerTracker _tracker;
	private readonly IHealthMonitorService _health;

	public HealthController(CoordinatorConfig cfg, ISessionLedger ledger, IWorkerTracker tracker, IHealthMonitorService health)
		=> (_cfg, _ledger, _tracker, _health) = (cfg, ledger, tracker, health);

	[HttpGet("/health")]
	public IActionResult Health()
	{
		var nodes = new Dictionary<string, object>();
		foreach (var w in _cfg.Workers)
			nodes[w.Name] = new { healthy = _tracker.IsHealthy(w.Name), slots_total = w.Slots, slots_idle = _tracker.FreeSlotCount(w.Name), stuck_slots = 0 };
		return new JsonResult(new { status = _health.IsStoreHealthy ? "healthy" : "degraded", nodes, store = new { healthy = _health.IsStoreHealthy } });
	}

	[HttpGet("/status")]
	public IActionResult Status()
	{
		var sessions = _ledger.AllSessions().Values.ToList();
		var nodes = new Dictionary<string, object>();
		foreach (var w in _cfg.Workers)
			nodes[w.Name] = new { tracker_status = _tracker.GetStatus(w.Name), busy_duration_s = _tracker.GetElapsedSeconds(w.Name), slots_total = w.Slots, slots_idle = 0 };
		return new JsonResult(new { sessions = new { active = _ledger.ActiveCount, sessions }, routing_stats = new { total = CoordinatorMetrics.RequestsTotalAll.Value }, nodes });
	}
}

[ApiController]
public class SessionsController : ControllerBase
{
	private readonly ISessionLedger _ledger;
	private readonly IWorkerScheduler _scheduler;

	public SessionsController(ISessionLedger ledger, IWorkerScheduler scheduler)
		=> (_ledger, _scheduler) = (ledger, scheduler);

	[HttpGet("/sessions")]
	public IActionResult List() => new JsonResult(_ledger.AllSessions().Values.ToList());

	[HttpDelete("/sessions/{sessionId}")]
	public IActionResult Evict(string sessionId) { _ledger.MarkEvicted(sessionId); return new JsonResult(new { evicted = true }); }

	[HttpPost("/sessions/{sessionId}/migrate")]
	public async Task<IActionResult> Migrate(string sessionId, [FromBody] Dictionary<string, object> body, CancellationToken ct)
	{
		var target = body.TryGetValue("target", out var t) ? t.ToString() : null;
		if (string.IsNullOrEmpty(target))
			return BadRequest(new { error = "target worker name required" });

		try
		{
			var result = await _scheduler.MigrateSessionAsync(sessionId, target, ct);
			return new JsonResult(result);
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}
}
