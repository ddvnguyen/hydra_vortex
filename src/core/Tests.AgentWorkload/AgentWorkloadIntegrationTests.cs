namespace Tests.AgentWorkload;

/// <summary>
/// Integration tests for the full agent workload pipeline.
/// Uses SkippableFact — tests skip (not fail) when the live rig or CLI
/// binaries are unavailable. Pure-logic unit tests live in the separate
/// *ParsingTests.cs files.
///
/// These tests exercise:
/// - Full N-turn conversation via IAgentCliDriver
/// - HydraLogScraper log collection
/// - Pass/fail assertion table from docs/paseo-hydra-agent-test.md §3
/// </summary>
[Collection("AgentWorkload")]
public sealed class AgentWorkloadIntegrationTests
{
    /// <summary>
    /// §3 Criterion 1: cached_tokens climbs turn-over-turn.
    /// First turn has 0 cached tokens; subsequent turns must have increasing cache hits.
    /// </summary>
    [SkippableFact]
    public void CachedTokens_ClimbsTurnOverTurn()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable at localhost:9000");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");

        var driver = new PiCliDriver();
        var sessionId = $"test-cached-tokens-{Guid.NewGuid():N}";
        var results = new List<AgentTurnResult>();

        for (int i = 0; i < ScriptedConversation.FullTurns; i++)
        {
            var turn = driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
            results.Add(turn);
        }

        // Require all turns completed successfully
        Assert.Equal(ScriptedConversation.FullTurns, results.Count);
        foreach (var r in results)
        {
            Assert.Equal(0, r.ExitCode);
            Assert.True(r.IsValidJson, $"Turn output is not valid JSON: {r.RawOutput[..Math.Min(200, r.RawOutput.Length)]}");
        }

        // Turn 1: cached_tokens should be 0 (cold start)
        Assert.Equal(0, results[0].CachedTokens);

        // Turn 2+: cached_tokens should be > 0 (prefix reuse)
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i].CachedTokens > 0,
                $"Turn {i + 1}: cached_tokens={results[i].CachedTokens}, expected > 0 for warm slot reuse");
        }

        // Cached tokens should be non-decreasing (more context = more cache hits)
        for (int i = 2; i < results.Count; i++)
        {
            Assert.True(results[i].CachedTokens >= results[i - 1].CachedTokens,
                $"Turn {i + 1}: cached_tokens={results[i].CachedTokens} < turn {i} cached_tokens={results[i - 1].CachedTokens}");
        }
    }

    /// <summary>
    /// §3 Criterion 7: reasoning_content present when reasoning is on.
    /// With a thinking-capable model, each response should include reasoning_content.
    /// </summary>
    [SkippableFact]
    public void ReasoningContent_PresentWhenReasoningOn()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");

        var driver = new PiCliDriver();
        var sessionId = $"test-reasoning-{Guid.NewGuid():N}";

        // Run 3 turns to get past cold start
        AgentTurnResult? lastResult = null;
        for (int i = 0; i < 3; i++)
        {
            lastResult = driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        Assert.NotNull(lastResult);
        Assert.True(lastResult.IsValidJson, $"Last turn output is not valid JSON: {lastResult.RawOutput[..Math.Min(200, lastResult.RawOutput.Length)]}");
        Assert.True(lastResult.ReasoningContentPresent,
            "reasoning_content should be present in response JSON for thinking-capable models");
    }

    /// <summary>
    /// §3 Criterion 3: restore_kv_ms == 0 on warm turns.
    /// Turn 2+ should have zero KV restore time (full prefix reuse).
    /// Requires positive evidence: the scrape must actually return events.
    /// </summary>
    [SkippableFact]
    public void RestoreKvMs_ZeroOnWarmTurns()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-restore-kv-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < ScriptedConversation.FullTurns; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;
        var timelineEvents = scraper.ScrapeRequestTimeline(runStart, runEnd);

        // Require positive evidence: at least 2 events (1 cold + 1 warm)
        Assert.True(timelineEvents.Count >= 2,
            $"Expected at least 2 request_timeline events (cold + warm turns), found {timelineEvents.Count}. " +
            "An empty scrape means no events were captured, not that restore_kv_ms is zero.");

        // Warm turns (skip first) should have restore_kv_ms == 0
        for (int i = 1; i < timelineEvents.Count; i++)
        {
            Assert.Equal(0f, timelineEvents[i].RestoreKvMs);
        }
    }

    /// <summary>
    /// §3 Criterion 4: queue_wait_ms ≈ 0 for a single agent.
    /// With no competing agents, queue wait should be negligible.
    /// </summary>
    [SkippableFact]
    public void QueueWaitMs_NearZeroForSingleAgent()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-queue-wait-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;
        var timelineEvents = scraper.ScrapeRequestTimeline(runStart, runEnd);

        // Require positive evidence
        Assert.NotEmpty(timelineEvents);

        // All requests should have near-zero queue wait (single agent, no contention)
        // Allow up to 5 seconds to account for first-request slot assignment
        foreach (var evt in timelineEvents.Skip(1))
        {
            Assert.True(evt.QueueWaitMs < 5000f,
                $"queue_wait_ms={evt.QueueWaitMs}ms, expected < 5000ms for single agent");
        }
    }

    /// <summary>
    /// §3 Criterion 5: throughput within ~2× of CLAUDE.md baselines.
    /// RTX 5060 Ti ~200 tok/s, RTX 3060 ~60 tok/s, P100 28 tok/s.
    /// </summary>
    [SkippableFact]
    public void Throughput_WithinBaselineTolerance()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-throughput-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;
        var timelineEvents = scraper.ScrapeRequestTimeline(runStart, runEnd);

        // Require positive evidence
        Assert.NotEmpty(timelineEvents);

        foreach (var evt in timelineEvents.Where(e => e.Status == "done"))
        {
            var throughput = HydraLogScraper.ComputeThroughput(evt);
            var baseline = evt.Node switch
            {
                "rtx5060ti" or "rtx" => ThroughputBaselines.Rtx5060Ti,
                "rtx3060" => ThroughputBaselines.Rtx3060,
                "p100" => ThroughputBaselines.P100,
                _ => 0f,
            };

            if (baseline > 0)
            {
                var minAcceptable = baseline / ThroughputBaselines.ToleranceMultiplier;
                Assert.True(throughput >= minAcceptable,
                    $"Node {evt.Node}: throughput={throughput:F1} tok/s, " +
                    $"baseline={baseline} tok/s, min acceptable={minAcceptable} tok/s " +
                    $"(tokens_out={evt.TokensOut}, decode_ms={evt.DecodeMs})");
            }
        }
    }

    /// <summary>
    /// §3 Criterion 6: no engine restarts during the run.
    /// Requires positive evidence that the scrape window contained real activity
    /// before concluding "no restarts found".
    /// </summary>
    [SkippableFact]
    public void NoEngineRestarts_DuringRun()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-restarts-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;

        // Positive evidence: confirm the scrape window captured real activity.
        // If no request_timeline events were scraped, we cannot distinguish
        // "no restarts" from "scrape returned nothing".
        var timelineEvents = scraper.ScrapeRequestTimeline(runStart, runEnd);
        Assert.True(timelineEvents.Count > 0,
            $"Scrape returned {timelineEvents.Count} request_timeline events — " +
            "cannot conclude 'no restarts' without evidence the window contained real activity. " +
            "Check container names and time window.");

        var crashEvents = scraper.ScrapeCrashRestart(runStart, runEnd);
        var restarts = crashEvents.Where(e => e.EventType == "attempting_restart").ToList();
        Assert.Empty(restarts);
    }

    /// <summary>
    /// §3 Criterion 8: route_type matches intent.
    /// With an explicit model (not hydra-auto), route_type should be consistent.
    /// </summary>
    [SkippableFact]
    public void RouteType_MatchesIntent()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-route-type-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;
        var timelineEvents = scraper.ScrapeRequestTimeline(runStart, runEnd);

        // Require positive evidence
        Assert.NotEmpty(timelineEvents);

        // First turn may be affinity (cold start); subsequent should be affinity (session stickiness)
        // No migration should occur for a single sequential agent
        foreach (var evt in timelineEvents.Skip(1))
        {
            Assert.DoesNotContain("migration", evt.RouteType);
        }
    }

    /// <summary>
    /// §3 Criterion 2: n_common fires on turns ≥ 2.
    /// The head container should log N_COMMON for warm-slot requests.
    /// </summary>
    [SkippableFact]
    public void NCommon_FiresOnWarmTurns()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver();
        var scraper = new HydraLogScraper();
        var sessionId = $"test-n-common-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < ScriptedConversation.FullTurns; i++)
        {
            driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
        }

        var runEnd = DateTimeOffset.UtcNow;
        var kvEvents = scraper.ScrapeKvReuse(runStart, runEnd);

        var nCommonEvents = kvEvents.Where(e => e.EventType == "N_COMMON").ToList();
        Assert.True(nCommonEvents.Count >= ScriptedConversation.FullTurns - 1,
            $"Expected at least {ScriptedConversation.FullTurns - 1} N_COMMON events " +
            $"(one per warm turn), found {nCommonEvents.Count}");
    }

    /// <summary>
    /// Full scripted conversation: all turns complete without error.
    /// This is the integration gate — if this skips, all other live tests also skip.
    /// </summary>
    [SkippableFact]
    public void FullScriptedConversation_AllTurnsComplete()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");

        var driver = new PiCliDriver();
        var sessionId = $"test-full-convo-{Guid.NewGuid():N}";
        var results = new List<AgentTurnResult>();

        for (int i = 0; i < ScriptedConversation.FullTurns; i++)
        {
            var result = driver.RunTurnAsync(sessionId, ScriptedConversation.Prompts[i])
                .GetAwaiter().GetResult();
            results.Add(result);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.IsValidJson, $"Turn {i + 1} output is not valid JSON");
            Assert.NotNull(result.ResponseContent);
            Assert.True(result.CompletionTokens > 0,
                $"Turn {i + 1}: completion_tokens={result.CompletionTokens}, expected > 0");
        }
    }
}
