using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Hydra.Core.Services;

public sealed class SessionEvictionService : BackgroundService
{
	private readonly CoordinatorConfig _cfg;
	private readonly ISessionLedger _ledger;
	private readonly IWorkerTracker _tracker;
	private readonly IWorkerScheduler _scheduler;
	private readonly ILogger _log;

	public SessionEvictionService(
		CoordinatorConfig cfg, ISessionLedger ledger,
		IWorkerTracker tracker, IWorkerScheduler scheduler, ILogger log)
	{
		_cfg = cfg; _ledger = ledger; _tracker = tracker; _scheduler = scheduler; _log = log;
	}

	protected override async Task ExecuteAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(60), ct);
			}
			catch (OperationCanceledException) { break; }

			try
			{
				var staleIds = _ledger.GetStaleSessionIds(_cfg.SessionIdleTimeoutS);
				foreach (var sid in staleIds)
				{
					var entry = _ledger.Lookup(sid);
					if (entry == null || entry.SlotFreed)
						continue;

					if (entry.NodeName.Length > 0)
					{
						try
						{
							await _scheduler.EvictWarmSessionAsync(sid, entry.NodeName, ct);
							// Issue #306: count watchdog reclaims so the bench
							// suite can correlate warm-hit rate with the
							// eviction rate. Non-zero is expected; a rate
							// higher than the warm-hit rate is the canary
							// for S10.
							//
							// Note: this counter is incremented on EVERY
							// successful reclaim, not only on "stuck" leases
							// (the SessionIdleTimeoutS is conservative enough
							// that virtually every reclaim is a normal idle
							// eviction, not a stuck-lease recovery). The
							// "stuck" in the metric name is a misnomer —
							// rename to `hydra_warm_leases_reclaimed_total`
							// tracked in review #307. The canary in
							// issue #306 is rate(reclaimed) vs rate(warm
							// sessions), so the signal is in the ratio, not
							// the absolute count.
							CoordinatorMetrics.StuckWarmLeases.Inc();
							continue;
						}
						catch (Exception ex)
						{
							_log.Warning(ex, "warm_eviction_failed Sid={Sid} fallback", sid);
						}
					}

					_ledger.MarkEvicted(sid);
					_tracker.Release(entry.NodeName);
				}

				if (staleIds.Count > 0)
					_log.Information("eviction_cycle Evicted={Count}", staleIds.Count);
			}
			catch (Exception ex)
			{
				_log.Warning(ex, "eviction_cycle_failed");
			}
		}
	}
}
