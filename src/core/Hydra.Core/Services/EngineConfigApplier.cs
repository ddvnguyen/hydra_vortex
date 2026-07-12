using System.Collections.Concurrent;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Shared;
using Serilog;

namespace Hydra.Core.Services;

/// <summary>
/// Service that pushes an <see cref="EngineConfig"/> (from
/// <see cref="ModelRegistry"/>) to the engine via 0x40 EngineConfigure.
///
/// Phase 2b of ddvnguyen/llama.cpp#36. Called at:
///   - engine startup (one-shot, ensures the engine is in the expected
///     state — the safety net for the T2/T3 deferred-rebuild path
///     per ddvnguyen/hydra_vortex#406 Q4)
///   - profile switch (the explicit trigger; e.g.
///     <c>bash scripts/set-profile.sh dense</c> now goes through
///     this path instead of an engine restart)
///   - first request to a worker that hasn't been configured yet
///     (lazy; covered by the startup path in practice)
///
/// Wire format: the engine's 0x40 handler (ddvnguyen/llama.cpp#41)
/// returns <c>{success, tier, params_applied, deferred_keys, error}</c>;
/// the typed <see cref="EngineConfigureResult"/> is parsed by
/// <see cref="HydraEngineClient.ParseConfigureResponse"/>.
/// </summary>
public sealed class EngineConfigApplier
{
    private readonly HydraEngineClient _client;
    private readonly ILogger _log;
    private readonly IWorkerTracker _tracker;
    private readonly ConcurrentDictionary<string, EngineConfig> _lastApplied = new();

    public EngineConfigApplier(
        HydraEngineClient client,
        IWorkerTracker tracker,
        ILogger log)
    {
        _client = client;
        _tracker = tracker;
        _log = log;
    }

    /// <summary>
    /// Push the <see cref="EngineConfig"/> to the engine at slot 0.
    /// The T1 fields apply immediately; T2/T3 fields are deferred
    /// to the engine's next slot-free moment (per the wire schema).
    /// </summary>
    public async Task<EngineConfigureResult> ApplyAsync(
        WorkerConfig head, EngineConfig config, string traceId, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(config.ToHydraConfigDict());
        _log.Information(
            "engine_config_apply Start={Start} Head={Head} Alias={Alias} Wire={Wire}",
            head.Name, head.Name, config.ModelAlias, json);
        var result = await _client.EngineConfigureAsync("0", json, traceId, ct);
        if (result.Success)
        {
            _lastApplied[head.Name] = config;
            if (result.HasDeferredChanges)
            {
                _log.Information(
                    "engine_config_deferred Head={Head} Tier={Tier} Deferred={Deferred}",
                    head.Name, result.Tier, string.Join(",", result.DeferredKeys));
            }
            else
            {
                _log.Information(
                    "engine_config_applied Head={Head} Tier={Tier} Params={Params}",
                    head.Name, result.Tier,
                    string.Join(",", result.ParamsApplied.Keys));
            }
        }
        else
        {
            _log.Warning(
                "engine_config_failed Head={Head} Alias={Alias} Error={Error}",
                head.Name, config.ModelAlias, result.Error ?? "(no error message)");
        }
        return result;
    }

    /// <summary>The most recently applied config for the named worker, or null.</summary>
    public EngineConfig? GetLastApplied(string workerName) =>
        _lastApplied.TryGetValue(workerName, out var c) ? c : null;
}
