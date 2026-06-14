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

		// Trace: dump full request params
		Console.Error.WriteLine($"event=request_full {System.Text.Json.JsonSerializer.Serialize(body)}");

		try
		{
			var result = await _scheduler.SubmitAsync(body, messages, sessionId, summary.EstimatedTokens, maxTokens, summary.PrefixHash, ct);
			if (result is IAsyncEnumerable<byte[]> stream)
			{
				Response.ContentType = "text/event-stream";
				Response.Headers["X-Hydra-Node"] = _scheduler.LastDispatchedNode ?? "unknown";
				await foreach (var chunk in stream.WithCancellation(ct))
				{
					await Response.Body.WriteAsync(chunk, ct);
				}
				return new EmptyResult();
			}

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
			// Always release the decode slot, even on client cancel/disconnect.
			_scheduler.NotifyStreamComplete(sessionId);
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
			nodes[w.Name] = new { healthy = _tracker.IsHealthy(w.Name), slots_total = w.Slots, slots_idle = _ledger.ActiveCountOnNode(w.Name) == 0 ? w.Slots : 0, stuck_slots = 0 };
		return new JsonResult(new { status = _health.IsStoreHealthy ? "healthy" : "degraded", nodes, store = new { healthy = _health.IsStoreHealthy } });
	}

	[HttpGet("/status")]
	public IActionResult Status()
	{
		var sessions = _ledger.AllSessions().Values.ToList();
		var nodes = new Dictionary<string, object>();
		foreach (var w in _cfg.Workers)
			nodes[w.Name] = new { tracker_status = _tracker.GetStatus(w.Name), busy_duration_s = _tracker.GetElapsedSeconds(w.Name), slots_total = w.Slots, slots_idle = 0 };
		return new JsonResult(new { sessions = new { active = _ledger.ActiveCount, sessions }, routing_stats = new { total = 0 }, nodes });
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
