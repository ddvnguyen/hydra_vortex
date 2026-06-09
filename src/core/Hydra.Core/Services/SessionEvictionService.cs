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
