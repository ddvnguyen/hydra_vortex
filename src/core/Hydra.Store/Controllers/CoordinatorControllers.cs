using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Hydra.Store.Models;
using Hydra.Store.Repositories;
using Hydra.Store.Services;

namespace Hydra.Store.Controllers;

[ApiController]
public class CompletionsController : ControllerBase
{
	static JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
	private readonly IWorkerScheduler _scheduler;

	public CompletionsController(IWorkerScheduler scheduler) => _scheduler = scheduler;

	[HttpPost("/v1/chat/completions")]
	public async Task<IActionResult> ChatCompletions(CancellationToken ct)
	{
		Dictionary<string, object> body;
		try
		{
			using var reader = new StreamReader(Request.Body, Encoding.UTF8);
			body = JsonSerializer.Deserialize<Dictionary<string, object>>(await reader.ReadToEndAsync(ct), _jsonOpts)!;
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

		int maxTokens = body.TryGetValue("max_tokens", out var mt) && mt is JsonElement mte ? mte.GetInt32() : 512;
		string? sessionId = body.TryGetValue("session_id", out var sid) && sid is JsonElement side ? side.GetString() : null;
		sessionId ??= Router.DeriveSessionId(messages);
		var prefixHash = Router.ComputePrefixHash(messages);

		try
		{
			var result = await _scheduler.SubmitAsync(body, messages, sessionId, maxTokens, prefixHash, ct);
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

	public SessionsController(ISessionLedger ledger) => _ledger = ledger;

	[HttpGet("/sessions")]
	public IActionResult List() => new JsonResult(_ledger.AllSessions().Values.ToList());

	[HttpDelete("/sessions/{sessionId}")]
	public IActionResult Evict(string sessionId) { _ledger.MarkEvicted(sessionId); return new JsonResult(new { evicted = true }); }
}
