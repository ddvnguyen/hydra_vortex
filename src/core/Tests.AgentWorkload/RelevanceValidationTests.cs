namespace Tests.AgentWorkload;

/// <summary>
/// Relevance validation tests (issue #596). The existing agent-workload suite
/// checks METRICS (cached tokens, throughput, route type, ...) but never
/// validates that the model's thinking, tool calls, and answers relate to the
/// question — so a pipeline corrupting the LLM output would go undetected.
/// These tests assert RELEVANCE: off-topic output is a Hydra-corruption signal.
///
/// Reality check (issue #588): the rig's 35B models do NOT reliably emit
/// tool_calls JSON. The calculator test therefore accepts EITHER a correct
/// tool call (strong check) OR a relevant text answer that engages with the
/// question's numbers (weak-but-valid check). A text answer that is off-topic
/// still fails.
///
/// Same gating as AgentWorkloadIntegrationTests: SkippableFact — tests skip
/// (not fail) when the live rig or CLI binaries are unavailable.
/// </summary>
[Collection("AgentWorkload")]
public sealed class RelevanceValidationTests
{
    private const string CalculatorResult = "7006652";

    /// <summary>
    /// §3 Criterion 9: relevance of tool calls / text answers (calculator).
    /// Turn 1 asks for 1234×5678; turn 2 references the earlier computation and
    /// asks for 5678×2. Each turn must either make the right tool call or
    /// answer with the question's numbers — never off-topic.
    /// </summary>
    [SkippableFact]
    public void CalculatorRelevance_ToolOrText()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable at localhost:9000");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");

        var driver = new PiCliDriver();
        var sessionId = $"test-relevance-calc-{Guid.NewGuid():N}";

        var turn1 = driver.RunTurnAsync(sessionId,
                "What is 1234 multiplied by 5678? Use the calculator tool if available " +
                "to get the exact answer, otherwise compute it and state the result.")
            .GetAwaiter().GetResult();

        Assert.Equal(0, turn1.ExitCode);
        Assert.True(turn1.IsValidJson,
            $"Turn 1 output is not valid JSON: {Truncate(turn1.RawOutput)}");
        Assert.NotNull(turn1.ResponseContent);

        if (turn1.ToolCallsPresent)
        {
            // Strong check: a tool call must target the question's operands
            // (or already carry the result). Wrong args = corruption signal.
            Assert.NotNull(turn1.ToolCallArgs);
            var args = turn1.ToolCallArgs!;
            Assert.True(
                (args.Contains("1234") && args.Contains("5678"))
                    || args.Contains(CalculatorResult),
                $"Turn 1 tool_call args '{args}' (name='{turn1.ToolCallName}') do not " +
                $"match the question's operands (1234, 5678) or result ({CalculatorResult})");
        }
        else
        {
            // Weak-but-valid check: no tool call, but the text answer still
            // engages with the question — correct result OR both operands.
            // Off-topic text (neither) is the Hydra-corruption signal.
            var answer = turn1.ResponseContent!;
            Assert.True(
                answer.Contains(CalculatorResult)
                    || (answer.Contains("1234") && answer.Contains("5678")),
                $"Turn 1 text answer does not reference the question's numbers " +
                $"(operands 1234/5678 or result {CalculatorResult}): {Truncate(answer)}");
        }

        var turn2 = driver.RunTurnAsync(sessionId,
                "You computed 1234*5678 earlier. What is 5678*2?")
            .GetAwaiter().GetResult();

        Assert.Equal(0, turn2.ExitCode);
        Assert.True(turn2.IsValidJson,
            $"Turn 2 output is not valid JSON: {Truncate(turn2.RawOutput)}");
        Assert.NotNull(turn2.ResponseContent);

        var answer2 = turn2.ResponseContent!;
        // Relevant: answers the new question (11356) or at least engages with
        // its operand (5678). Staleness guard: repeating turn 1's operands AND
        // result without answering turn 2 means the model ignored the new
        // question — a multi-turn context failure.
        var answered = answer2.Contains("11356") || answer2.Contains("5678");
        var repeatedTurn1Answer = answer2.Contains("1234")
            && answer2.Contains(CalculatorResult)
            && !answer2.Contains("11356");
        Assert.True(answered && !repeatedTurn1Answer,
            $"Turn 2 answer is off-topic or repeats turn 1's answer " +
            $"(expected 5678*2=11356 or operand 5678): {Truncate(answer2)}");
    }

    /// <summary>
    /// §3 Criterion 10: hydra-auto routes through the auto-resolver.
    /// A fresh session with model=hydra-auto must yield a valid response AND a
    /// concrete worker plan in the coordinator log. Cold expectation per
    /// models.json: moe-35b-pd (tier 2) with Mode=pd (P/D split).
    /// </summary>
    [SkippableFact]
    public void HydraAuto_RoutesViaAutoResolver()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable at localhost:9000");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");
        Skip.IfNot(LiveRigGuard.IsPodmanAvailable(), "podman not available for log scraping");

        var driver = new PiCliDriver(model: "hydra-auto");
        var scraper = new HydraLogScraper();
        var sessionId = $"test-hydra-auto-{Guid.NewGuid():N}";
        var runStart = DateTimeOffset.UtcNow;

        var turn = driver.RunTurnAsync(sessionId,
                "Respond with exactly the sentence: the hydra auto router works.")
            .GetAwaiter().GetResult();

        var runEnd = DateTimeOffset.UtcNow;

        Assert.Equal(0, turn.ExitCode);
        Assert.True(turn.IsValidJson,
            $"hydra-auto turn output is not valid JSON: {Truncate(turn.RawOutput)}");
        Assert.False(string.IsNullOrWhiteSpace(turn.ResponseContent),
            "hydra-auto turn returned an empty response");

        var autoRouteEvents = scraper.ScrapeAutoRoute(runStart, runEnd);
        Assert.True(autoRouteEvents.Count > 0,
            "No autoroute_resolved events in the run window — the auto-resolver " +
            "did not resolve this session to a concrete plan. (Response validity " +
            "above already passed; a log-scrape failure should not silently pass " +
            "routing verification, so this requires positive evidence.)");

        // Correlate OUR request to its routing event. The coordinator session
        // id is a hash of the pi --session-id value, so match via the
        // model_routing_check line for the hydra-auto model instead of raw
        // equality. Fall back to any auto-resolve event in the window only if
        // the correlation fails (log-format drift) — the AgentWorkload
        // collection is serialized and the rig reserved, so window pollution
        // is unlikely.
        var coordinatorSid = scraper.FindSessionIdForModel(runStart, runEnd, "hydra-auto");
        var mine = coordinatorSid is not null
            ? autoRouteEvents.Where(e => e.Sid == coordinatorSid).ToList()
            : [];
        if (mine.Count == 0)
            mine = autoRouteEvents.ToList();

        var resolved = mine[0];
        Assert.True(!string.IsNullOrEmpty(resolved.Model)
            && resolved.Model != "hydra-auto",
            $"autoroute_resolved Model='{resolved.Model}' is not a concrete alias: " +
            $"{resolved.RawLine}");

        // Cold-session plan per infra/hydra-core/config/models.json:
        // tier 2 moe-35b-pd with P/D split (decode worker on p100).
        Assert.Equal("moe-35b-pd", resolved.Model);
        Assert.Equal("pd", resolved.Mode);
        Assert.NotEqual("none", resolved.Decode);
    }

    /// <summary>
    /// §3 Criterion 9: thinking relevance. With a reasoning-capable model the
    /// reasoning must engage with the question's topic. Empty or off-topic
    /// reasoning while the model is healthy is a corruption signal.
    /// </summary>
    [SkippableFact]
    public void ThinkingRelevance_MentionsPromptKeywords()
    {
        Skip.IfNot(LiveRigGuard.IsHydraReachable(), "Hydra rig not reachable at localhost:9000");
        Skip.IfNot(LiveRigGuard.IsCliAvailable("pi"), "pi CLI not found on PATH");

        var driver = new PiCliDriver();
        var sessionId = $"test-relevance-thinking-{Guid.NewGuid():N}";

        var turn = driver.RunTurnAsync(sessionId,
                "Explain the trade-offs of KV cache quantization in one paragraph. " +
                "Think step by step before answering.")
            .GetAwaiter().GetResult();

        Assert.Equal(0, turn.ExitCode);
        Assert.True(turn.IsValidJson,
            $"Turn output is not valid JSON: {Truncate(turn.RawOutput)}");
        Assert.NotNull(turn.ResponseContent);

        // Reasoning must be present AND non-empty for a thinking model.
        Assert.True(turn.ReasoningContentPresent
            && !string.IsNullOrWhiteSpace(turn.ReasoningContent),
            "reasoning_content must be present and non-empty for a " +
            "thinking-capable model (empty = fields being dropped)");

        // And it must engage with the question's topic (KV cache quantization).
        var reasoning = turn.ReasoningContent!.ToLowerInvariant();
        Assert.True(
            reasoning.Contains("quant")
                || reasoning.Contains("kv")
                || reasoning.Contains("cache"),
            $"Reasoning does not reference the question's topic (KV cache " +
            $"quantization) — off-topic thinking is a corruption signal: " +
            $"{Truncate(turn.ReasoningContent!)}");
    }

    private static string Truncate(string s, int maxLength = 200) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}
