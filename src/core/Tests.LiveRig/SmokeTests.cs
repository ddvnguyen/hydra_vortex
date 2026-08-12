using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Post-deploy smoke set (epic #470) — the fast feedback loop for the live
/// rig. 5 tests, ~10 minutes total, each staying around 2K context
/// (~1K-token prompt, 1K-2K output cap) so a deploy can be verified quickly
/// without paying the full suite's runtime.
///
/// Runs alone via the `smoke` tier in .github/workflows/test-live-rig.yml
/// (--filter "FullyQualifiedName~SmokeTests"). Mirrors the helper/fixture
/// conventions of the rest of Tests.LiveRig (shared HttpClient, SkippableFact,
/// session cleanup in finally).
///
/// Coverage intent (one path per test, no overlap):
///   1. Smoke_WarmAffinityMultiturn  — warm-affinity KV restore across turns
///   2. Smoke_StreamingReasoningContent — #616/#622 streaming content fallback
///   3. Smoke_PdMixQuantMultiTurn    — P/D split mix-quant (rtx prefill → p100
///      quant decode) multi-turn, the epic #470 headline feature
///   4. Smoke_MigrationContinuation  — rtx→p100 migration + cache-hit restore
///   5. Smoke_ToolCall               — one tool-call round-trip
///   6. Smoke_DenseMultiturnTiming   — dense-combined warm timing budget (FIX-3)
/// </summary>
[Collection("LiveRig")]
public sealed class SmokeTests : IClassFixture<LiveRigFixture>
{
    private const string SystemPrompt =
        "You are an expert software engineer specialising in distributed systems and GPU inference. " +
        "Provide concise, accurate answers.";

    /// <summary>Prompt sizing floor: ~1K tokens via the shared GenerateText helper (3 chars/token).</summary>
    private const int PromptTokens = 1_000;

    /// <summary>Output cap: 1.5K tokens ≈ 20-75s rtx / 55-125s p100 worst case.</summary>
    private const int MaxTokens = 1_500;

    private readonly LiveRigFixture _fx;

    public SmokeTests(LiveRigFixture fx) => _fx = fx;

    private static string MakeSessionId(string prefix) => $"smoke-{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// Realistic ~1K-token user prompt: filler context (LargePrompt builder
    /// convention) plus a concrete instruction. The filler is trimmed to
    /// PromptTokens - 30 so the instruction fits the target context.
    /// </summary>
    private static string MakeUserPrompt(string instruction)
    {
        var padding = HttpHelpers.GenerateText(PromptTokens - 30);
        return $"{padding}\n\n{instruction}";
    }

    private async Task<JsonElement> SendCompletion(
        string sessionId,
        List<Dictionary<string, object?>> messages,
        int maxTokens = MaxTokens,
        int timeoutSec = 300,
        List<JsonElement>? tools = null,
        string? toolChoice = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
        if (tools != null) body["tools"] = tools;
        if (toolChoice != null) body["tool_choice"] = toolChoice;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ExtractContent(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "";
        return HttpHelpers.GetOutputText(choices[0].GetProperty("message")).Trim();
    }

    private static (double ModelLoadMs, double RestoreSlotMs, double PromptMs, double DecodeMs, double NPast)?
        TryExtractTurnMetrics(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("hydra_metrics", out var hm) || hm.ValueKind != JsonValueKind.Object)
            return null;
        double GetNum(string name) =>
            hm.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
        return (GetNum("model_load_ms"), GetNum("restore_slot_ms"), GetNum("prompt_ms"),
            GetNum("decode_ms"), GetNum("n_past"));
    }

    /// <summary>usage.prompt_tokens / usage.completion_tokens from a response (0 when absent).</summary>
    private static (int PromptTokens, int CompletionTokens) ExtractUsage(JsonElement responseJson)
    {
        int GetInt(string name) =>
            responseJson.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var p) ? p.GetInt32() : 0;
        return (GetInt("prompt_tokens"), GetInt("completion_tokens"));
    }

    /// <summary>
    /// Eval-verify: the model's reply must be ON-TOPIC for the question asked.
    /// Each group lists accepted keywords; the reply must contain at least one
    /// keyword from EVERY group (case-insensitive). Groups let us accept
    /// synonyms/variants ("rate limit" ~ "throttl") without weakening the check.
    /// This is the #596 relevance criterion applied to the smoke set: a
    /// non-empty reply that ignores the question is a model/stack failure.
    /// </summary>
    private static void AssertOnTopic(string question, string reply, params string[][] keywordGroups)
    {
        Assert.False(string.IsNullOrEmpty(reply), $"Eval-verify: empty reply for question '{question}'");
        var lower = reply.ToLowerInvariant();
        var missing = new List<string>();
        foreach (var group in keywordGroups)
        {
            if (!group.Any(k => lower.Contains(k.ToLowerInvariant())))
                missing.Add($"none of [{string.Join(" | ", group)}]");
        }
        Assert.True(missing.Count == 0,
            $"Eval-verify failed — reply not on-topic for question '{question}': {string.Join("; ", missing)}. " +
            $"Got: {reply[..Math.Min(300, reply.Length)]}");
    }

    // ── Tests ────────────────────────────────────────────────────────────

    /// <summary>
    /// Warm-affinity KV restore across 3 turns (prompt ~1K each, ctx grows to
    /// ~3K). Asserts: non-empty content every turn, n_past grows (cache
    /// accumulates), and — when hydra_metrics rides through — no model reload
    /// on turns 2-3 (model_load_ms == 0, the restore path). Wall-clock sanity:
    /// turns 2-3 must not blow 3x turn 1 (mirrors FiveTurnWarmAffinity).
    /// </summary>
    [SkippableFact(Timeout = 600_000)]
    public async Task Smoke_WarmAffinityMultiturn()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("warm");
        var history = new List<Dictionary<string, object?>>();
        var turnTimes = new List<double>();
        var nodes = new List<string?>();
        var nPastList = new List<int>();

        try
        {
            var instructions = new[]
            {
                "Summarize the main theme of the context above in one paragraph.",
                "Now explain the first key concept from the context above in one paragraph.",
                "Now explain the second key concept from the context above in one paragraph.",
            };
            // Eval-verify per turn: the answer must stay on-topic with the
            // question. The filler context is generic distributed-systems
            // prose (HttpHelpers.GenerateText), so a valid summary/explanation
            // must reference at least one concept from that domain.
            var evalTerms = new[]
            {
                new[] { "theme", "main", "distribut", "system", "software", "engineer", "concept" },
                new[] { "distribut", "system", "scalab", "concurr", "consisten", "message", "queue", "cache", "rpc", "network", "replica", "partition", "shard", "stream", "rate" },
                new[] { "distribut", "system", "scalab", "concurr", "consisten", "message", "queue", "cache", "rpc", "network", "replica", "partition", "shard", "stream", "rate" },
            };
            for (var turn = 0; turn < instructions.Length; turn++)
            {
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = MakeUserPrompt(instructions[turn]) },
                };
                var sw = Stopwatch.StartNew();
                var resp = await SendCompletion(sessionId, messages);
                sw.Stop();
                turnTimes.Add(sw.Elapsed.TotalSeconds);

                var reply = ExtractContent(resp);
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                // Eval-verify: reply must reference the question's focus.
                AssertOnTopic(instructions[turn], reply, evalTerms[turn]);
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                var (promptTok, completionTok) = ExtractUsage(resp);
                var metrics = TryExtractTurnMetrics(resp);
                var status = await _fx.GetStatusAsync();
                var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                nodes.Add(session?.Node);
                nPastList.Add(session?.NPast ?? 0);

                Console.WriteLine(
                    $"Smoke_WarmAffinityMultiturn: turn={turn + 1} node={session?.Node ?? "?"} " +
                    $"duration_ms={sw.Elapsed.TotalMilliseconds:F0} prompt_tokens={promptTok} " +
                    $"completion_tokens={completionTok} chars={reply.Length} n_past={session?.NPast ?? 0} " +
                    $"model_load_ms={metrics?.ModelLoadMs ?? -1} restore_slot_ms={metrics?.RestoreSlotMs ?? -1}");

                if (turn > 0)
                {
                    // Restore path: no reload on a warm slot (skip only if
                    // hydra_metrics is absent — the wall-clock budget still guards).
                    if (metrics is not null)
                        Assert.True(metrics.Value.ModelLoadMs == 0,
                            $"Turn {turn + 1}: unexpected model reload — model_load_ms={metrics.Value.ModelLoadMs:F0} " +
                            "(warm-affinity restore expected, not a reload)");

                    // Cache accumulates: n_past must keep growing across turns.
                    Assert.True(session?.NPast is int np && np > nPastList[turn - 1],
                        $"Turn {turn + 1}: n_past did not grow ({nPastList[turn - 1]} → {session?.NPast ?? 0}) " +
                        "— KV cache was evicted or reset between turns");
                }
            }

            // Timing sanity (mirror FiveTurnWarmAffinity): turns 2-3 must not
            // be catastrophically slower than turn 1 (full re-prefill regression).
            for (var i = 1; i < turnTimes.Count; i++)
                Assert.True(turnTimes[i] < turnTimes[0] * 3,
                    $"Turn {i + 1} took {turnTimes[i]:F1}s vs turn-1 {turnTimes[0]:F1}s (3x threshold) — " +
                    "likely a full re-prefill or migration regression");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Streaming #616/#622 fix validation: with a ~1K prompt and 1.5K output
    /// cap, collect ALL SSE events and assert content OR reasoning_content is
    /// present. When reasoning_content appears at all (native emission on a
    /// reasoning model), assert the concatenated reasoning is non-empty — the
    /// merged-decode drop of reasoning_content (#616) and the usage-less DONE
    /// delta fallback (#622) both fail this.
    /// </summary>
    [SkippableFact(Timeout = 600_000)]
    public async Task Smoke_StreamingReasoningContent()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("stream");
        try
        {
            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt },
                new() { ["role"] = "user", ["content"] = MakeUserPrompt(
                    "Reason step by step, then explain how GPU KV cache migration works in one paragraph.") },
            };
            var body = new Dictionary<string, object?>
            {
                ["messages"] = messages,
                ["max_tokens"] = MaxTokens,
                ["temperature"] = 0,
                ["stream"] = true,
                ["session_id"] = sessionId,
            };

            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var allOutputs = new List<string>();
            var reasoningParts = new List<string>();
            var sawReasoningKey = false;
            var eventCount = 0;
            var rawPayload = new System.Text.StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrEmpty(line)) continue;
                // merged-decode streaming may return the buffered completion as
                // a single non-SSE JSON blob; keep non-"data:" lines as fallback.
                if (!line.StartsWith("data: "))
                {
                    rawPayload.Append(line);
                    continue;
                }
                var payload = line["data: ".Length..];
                if (payload == "[DONE]") break;
                eventCount++;
                try
                {
                    var ev = JsonSerializer.Deserialize<JsonElement>(payload);
                    if (ev.TryGetProperty("choices", out var evChoices) && evChoices.GetArrayLength() > 0)
                    {
                        var delta = evChoices[0].GetProperty("delta");
                        if (delta.TryGetProperty("reasoning_content", out var rc))
                        {
                            sawReasoningKey = true;
                            if (rc.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rc.GetString()))
                                reasoningParts.Add(rc.GetString()!);
                        }
                        var content = HttpHelpers.GetOutputText(delta);
                        if (!string.IsNullOrEmpty(content)) allOutputs.Add(content);
                    }
                }
                catch { /* skip malformed events */ }
            }

            // Fallback: no SSE deltas — parse the buffered blob as a plain
            // completion (also surfaces reasoning_content on the message).
            if (allOutputs.Count == 0 && reasoningParts.Count == 0 && !string.IsNullOrWhiteSpace(rawPayload.ToString()))
            {
                try
                {
                    var blob = JsonSerializer.Deserialize<JsonElement>(rawPayload.ToString());
                    if (blob.TryGetProperty("choices", out var blobChoices) && blobChoices.GetArrayLength() > 0)
                    {
                        var msg = blobChoices[0].GetProperty("message");
                        if (msg.TryGetProperty("reasoning_content", out var rcBlob))
                        {
                            sawReasoningKey = true;
                            if (rcBlob.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rcBlob.GetString()))
                                reasoningParts.Add(rcBlob.GetString()!);
                        }
                        var content = HttpHelpers.GetOutputText(msg);
                        if (!string.IsNullOrEmpty(content)) allOutputs.Add(content);
                    }
                }
                catch { /* not a JSON blob — keep empty */ }
            }

            sw.Stop();
            var combined = string.Concat(allOutputs);
            var combinedReasoning = string.Concat(reasoningParts);
            Console.WriteLine(
                $"Smoke_StreamingReasoningContent: elapsed_ms={sw.Elapsed.TotalMilliseconds:F0} " +
                $"events={eventCount} content_chars={combined.Length} " +
                $"reasoning_chars={combinedReasoning.Length} saw_reasoning_key={sawReasoningKey}");

            // #616/#622 validation: at least content OR reasoning_content must
            // have been emitted (a usage-less DONE delta with no generation
            // evidence is the regression this guards).
            Assert.True(combined.Length > 0 || combinedReasoning.Length > 0,
                $"No content or reasoning_content across {eventCount} stream events — " +
                "streaming generation evidence missing (see #616/#622)");

            // Eval-verify: the generated text must answer the question asked
            // (KV cache migration), not just produce tokens. The question
            // explicitly targets "GPU KV cache migration".
            var streamedText = string.Concat(combined, combinedReasoning);
            AssertOnTopic("explain how GPU KV cache migration works", streamedText,
                new[] { "kv", "cache", "migrat", "transfer", "gpu", "restore", "state", "key-value", "key value" });

            // Native emission validation: when reasoning_content appears, it
            // must actually carry tokens (not an empty placeholder key).
            if (sawReasoningKey)
                Assert.False(string.IsNullOrEmpty(combinedReasoning),
                    "reasoning_content key present but all deltas empty — native reasoning emission dropped");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// P/D split MIX-QUANT multi-turn (epic #470 headline feature): explicit
    /// model moe-35b-pd — precise prefill on rtx (Q3_K-mini) + quant decode
    /// on p100 (Q5_K-balanced, cross-quant). 3 turns, ~1K prompt each:
    ///   - every turn: non-empty content AND eval-verify (reply on-topic)
    ///   - turn 1: the session must actually land on p100 (decode_node=p100 —
    ///     a fallback to rtx solo means P/D routing regressed)
    ///   - turns 2+: n_past grows (KV accumulates across the P/D split),
    ///     wall-clock sanity vs turn 1 (no catastrophic re-prefill)
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task Smoke_PdMixQuantMultiTurn()
    {
        _fx.SkipIfUnreachable();
        const string pdModel = "moe-35b-pd";
        var sessionId = MakeSessionId("pd");
        var history = new List<Dictionary<string, object?>>();
        var turnTimes = new List<double>();
        var nPastList = new List<int>();
        var nodes = new List<string?>();

        try
        {
            var turns = new[]
            {
                "Explain in one paragraph how a P/D split (prefill/decode) system serves a large MoE model across two GPUs.",
                "Now explain what the KV cache stores during decode, in one paragraph.",
                "Now explain why decode benefits from a smaller quantized model, in one paragraph.",
            };
            var evalTerms = new[]
            {
                new[] { "prefill", "decode", "split", "p/d", "gpu", "moe", "expert", "kv", "transfer" },
                new[] { "kv", "cache", "key", "value", "token", "state", "attention", "store" },
                new[] { "quant", "decode", "small", "memory", "vram", "latency", "throughput", "token" },
            };
            for (var turn = 0; turn < turns.Length; turn++)
            {
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = MakeUserPrompt(turns[turn]) },
                };
                var sw = Stopwatch.StartNew();
                var resp = await SendCompletionDense(pdModel, sessionId, messages);
                sw.Stop();
                turnTimes.Add(sw.Elapsed.TotalSeconds);

                var reply = ExtractContent(resp);
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply on {pdModel}");
                // Eval-verify: reply must answer the question, not drift.
                AssertOnTopic(turns[turn], reply, evalTerms[turn]);
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                var (promptTok, completionTok) = ExtractUsage(resp);
                var status = await _fx.GetStatusAsync();
                var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                nodes.Add(session?.Node);
                nPastList.Add(session?.NPast ?? 0);
                Console.WriteLine(
                    $"Smoke_PdMixQuantMultiTurn: turn={turn + 1} node={session?.Node ?? "?"} " +
                    $"duration_ms={sw.Elapsed.TotalMilliseconds:F0} prompt_tokens={promptTok} " +
                    $"completion_tokens={completionTok} chars={reply.Length} n_past={session?.NPast ?? 0}");

                if (turn == 0)
                {
                    // The P/D contract: decode must be on p100 (cross-quant).
                    // A node==rtx here means the coordinator fell back to solo
                    // routing — the mix-quant split did not happen.
                    Assert.Equal("p100", session?.Node);
                }
                else
                {
                    // KV accumulates across turns on the P/D path.
                    Assert.True(session?.NPast is int np && np > nPastList[turn - 1],
                        $"Turn {turn + 1}: n_past did not grow ({nPastList[turn - 1]} → {session?.NPast ?? 0}) " +
                        "— KV cache was evicted or reset between P/D turns");
                }
            }

            // Wall-clock sanity: turns 2-3 must not be catastrophically slower
            // than turn 1 (full re-prefill regression, like WarmAffinity).
            for (var i = 1; i < turnTimes.Count; i++)
                Assert.True(turnTimes[i] < turnTimes[0] * 3,
                    $"Turn {i + 1} took {turnTimes[i]:F1}s vs turn-1 {turnTimes[0]:F1}s (3x threshold) — " +
                    "likely a full re-prefill or migration regression");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Migration continuation: turn 1 on the host node (prompt ~1K, output
    /// ~1K), force migration to p100 via /sessions/{id}/migrate, turn 2 on
    /// p100 with the restored KV. Asserts content + cache-hit restore
    /// (timings.cache_n > 0, prompt_ms < 5s — mirrors MigrationCacheHit) and
    /// that /status reports the session on p100.
    /// </summary>
    [SkippableFact(Timeout = 600_000)]
    public async Task Smoke_MigrationContinuation()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("migrate");
        try
        {
            // Turn 1: cold start on the default route (rtx).
            var turn1Msgs = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt },
                new() { ["role"] = "user", ["content"] = MakeUserPrompt(
                    "Describe the architecture of a multi-GPU LLM serving system in one paragraph.") },
            };
            var sw1 = Stopwatch.StartNew();
            var resp1 = await SendCompletion(sessionId, turn1Msgs);
            sw1.Stop();
            var reply1 = ExtractContent(resp1);
            Assert.False(string.IsNullOrEmpty(reply1), "Turn 1: empty reply");
            // Eval-verify: turn 1 must describe a multi-GPU LLM serving
            // architecture, not drift off-topic.
            AssertOnTopic("Describe the architecture of a multi-GPU LLM serving system", reply1,
                new[] { "gpu", "serv", "llm", "inference", "model", "worker", "node", "prefill", "decode", "engine", "architect", "distribut" });
            var (pt1, ct1) = ExtractUsage(resp1);
            Console.WriteLine(
                $"Smoke_MigrationContinuation: turn=1 elapsed_ms={sw1.Elapsed.TotalMilliseconds:F0} " +
                $"prompt_tokens={pt1} completion_tokens={ct1} chars={reply1.Length}");

            // Force migration to p100 (the explicit /migrate endpoint, same as
            // MigrationCacheHit / FullCycleCompletionMigrationContinuation).
            using var migrateCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var migrateResp = await HttpHelpers.Client.PostAsJsonAsync(
                $"{_fx.CoordUrl}/sessions/{sessionId}/migrate",
                new { target = "p100" }, migrateCts.Token);
            Assert.True(migrateResp.IsSuccessStatusCode,
                $"Migration failed: {await migrateResp.Content.ReadAsStringAsync()}");
            var migrateBody = await migrateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(migrateBody.GetProperty("migrated").GetBoolean());
            Assert.Equal("p100", migrateBody.GetProperty("target").GetString());

            // Turn 2: continuation on p100 — KV must be restored, not re-prefilled.
            var turn2Msgs = new List<Dictionary<string, object?>>(turn1Msgs)
            {
                new() { ["role"] = "assistant", ["content"] = reply1 },
                new() { ["role"] = "user", ["content"] = MakeUserPrompt(
                    "Now explain the second key component of that architecture in one paragraph.") },
            };
            var sw2 = Stopwatch.StartNew();
            var resp2 = await SendCompletion(sessionId, turn2Msgs);
            sw2.Stop();
            var reply2 = ExtractContent(resp2);
            Assert.False(string.IsNullOrEmpty(reply2), "Turn 2 (after migration): empty reply");
            // Eval-verify: turn 2 must explain a component of the same
            // architecture (second key component of a multi-GPU serving
            // system), staying on-topic after the migration hop.
            AssertOnTopic("explain the second key component of a multi-GPU LLM serving architecture", reply2,
                new[] { "gpu", "serv", "llm", "inference", "model", "worker", "node", "prefill", "decode", "engine", "cache", "kv", "schedul", "rout", "load", "store", "migrat" });
            var (pt2, ct2) = ExtractUsage(resp2);
            var timings = resp2.TryGetProperty("timings", out var t) ? t : default;
            var cacheN = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("cache_n", out var cn) ? cn.GetInt32() : 0;
            var promptMs = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("prompt_ms", out var pm) ? pm.GetDouble() : 0;
            Console.WriteLine(
                $"Smoke_MigrationContinuation: turn=2 node=p100 elapsed_ms={sw2.Elapsed.TotalMilliseconds:F0} " +
                $"prompt_tokens={pt2} completion_tokens={ct2} chars={reply2.Length} " +
                $"cache_n={cacheN} prompt_ms={promptMs:F0}");

            Assert.True(cacheN > 0,
                $"cache_n={cacheN} — KV cache was not used after migration to p100");
            Assert.True(promptMs < 5000,
                $"prompt_ms={promptMs:F0} — full re-prefill occurred instead of the cached restore path");

            var status = await _fx.GetStatusAsync();
            var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            Assert.True(session?.Node == "p100",
                $"Session not on p100 after migration (node={session?.Node ?? "?"})");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// One tool-call round-trip at ~1K context: model calls the calculator,
    /// we inject the result, the final answer must report it. Mirrors
    /// AgentWorkflowTests.ToolCallBasic (dual-branch: tool path strict, text
    /// path tolerant) with the ~1K prompt floor and 1.5K output cap.
    /// </summary>
    [SkippableFact(Timeout = 600_000)]
    public async Task Smoke_ToolCall()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("tool");
        try
        {
            var calculatorTool = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = "calculator",
                    description = "Evaluate a simple arithmetic expression and return the numeric result.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            expression = new { type = "string", description = "A math expression to evaluate, e.g. '1234 * 5678'" }
                        },
                        required = new[] { "expression" }
                    }
                }
            });

            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = MakeUserPrompt(
                    "What is 1234 multiplied by 5678? Use the calculator tool to get the exact answer.") },
            };

            // Turn 1: model should call the calculator; a verbose-thinking
            // model may instead answer in text — both branches are valid.
            var sw1 = Stopwatch.StartNew();
            var resp = await SendCompletion(sessionId, messages, tools: [calculatorTool], toolChoice: "auto");
            sw1.Stop();
            var (pt1, ct1) = ExtractUsage(resp);
            var choice = resp.GetProperty("choices")[0];
            var finishReason = choice.GetProperty("finish_reason").GetString();
            Console.WriteLine(
                $"Smoke_ToolCall: turn=1 finish_reason={finishReason} elapsed_ms={sw1.Elapsed.TotalMilliseconds:F0} " +
                $"prompt_tokens={pt1} completion_tokens={ct1}");

            if (finishReason == "tool_calls")
            {
                // Tool path: model requested the calculator — strict assertions.
                var toolCalls = choice.GetProperty("message").GetProperty("tool_calls");
                Assert.True(toolCalls.GetArrayLength() >= 1);
                Assert.Equal("calculator", toolCalls[0].GetProperty("function").GetProperty("name").GetString());

                var argsStr = toolCalls[0].GetProperty("function").GetProperty("arguments").GetString()!;
                var args = JsonSerializer.Deserialize<JsonElement>(argsStr);
                var expr = args.TryGetProperty("expression", out var e) ? e.GetString() ?? "0" : "0";
                var allowed = "0123456789+-*/(). ";
                var result = "error";
                if (expr.All(c => allowed.Contains(c)))
                {
                    var dt = new System.Data.DataTable();
                    result = dt.Compute(expr, null)?.ToString() ?? "error";
                }
                Assert.Equal("7006652", result);

                messages.Add(new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls });
                messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = toolCalls[0].GetProperty("id").GetString(), ["content"] = result });

                var sw2 = Stopwatch.StartNew();
                var resp2 = await SendCompletion(sessionId, messages, tools: [calculatorTool], toolChoice: "auto");
                sw2.Stop();
                var (pt2, ct2) = ExtractUsage(resp2);
                var choice2 = resp2.GetProperty("choices")[0];
                Assert.True(choice2.GetProperty("finish_reason").GetString() is "stop" or "length",
                    $"Turn 2: unexpected finish_reason={choice2.GetProperty("finish_reason").GetString()}");
                var answer = HttpHelpers.GetOutputText(choice2.GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(answer), "Turn 2: empty reply");
                Assert.Contains("7006652", answer.Replace(",", ""));
                Console.WriteLine(
                    $"Smoke_ToolCall: turn=2 elapsed_ms={sw2.Elapsed.TotalMilliseconds:F0} " +
                    $"prompt_tokens={pt2} completion_tokens={ct2} chars={answer.Length}");
            }
            else
            {
                // Text path: answer must reference operands/result.
                Assert.True(finishReason is "stop" or "length",
                    $"Turn 1: unexpected finish_reason={finishReason}");
                var answer = HttpHelpers.GetOutputText(choice.GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(answer), "Turn 1: empty reply");
                Assert.True((answer.Contains("1234") && answer.Contains("5678")) || answer.Contains("7006652"),
                    $"Expected operands 1234/5678 or result 7006652 in answer. Got: {answer[..Math.Min(300, answer.Length)]}");
                Console.WriteLine($"Smoke_ToolCall: text-path chars={answer.Length}");
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Dense-combined warm timing budget (#470 FIX-3), 3 turns to cover
    /// multi-turn continuation on the COMBINED pair. Turn 1 is the COLD
    /// baseline (pays the ~60-120s inline swap to dense-27b-combined;
    /// baseline_warm = duration − model_load_ms). Turns 2+ must NOT reload
    /// (model_load_ms == 0) and must fit baseline_warm + 10s — mirrors
    /// CombinedDenseTests.Dense27bMultiturn assertions with a ~1K prompt and
    /// 1.5K output cap. Every turn is eval-verified: the reply must be
    /// on-topic for the question asked (KV cache knowledge, not drift).
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task Smoke_DenseMultiturnTiming()
    {
        _fx.SkipIfUnreachable();
        const string denseModel = "dense-27b-combined";
        var sessionId = MakeSessionId("dense");
        var history = new List<Dictionary<string, object?>>();
        double? baselineWarmSec = null;
        double? turn1ModelLoadMs = null;

        try
        {
            var turns = new[]
            {
                "List three things KV cache stores. One sentence each.",
                "Now explain the first one in one sentence.",
                "Now explain the second one in one sentence.",
            };
            // Eval-verify per turn: the answer must be about KV cache / GPU
            // inference, anchored to the question's focus.
            var evalTerms = new[]
            {
                new[] { "kv", "cache", "key", "value", "token", "state", "attention", "store" },
                new[] { "kv", "cache", "key", "value", "token", "state", "attention" },
                new[] { "kv", "cache", "key", "value", "token", "state", "attention" },
            };
            for (var turn = 0; turn < turns.Length; turn++)
            {
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = MakeUserPrompt(turns[turn]) },
                };
                var sw = Stopwatch.StartNew();
                var resp = await SendCompletionDense(denseModel, sessionId, messages);
                sw.Stop();
                var durationSec = sw.Elapsed.TotalSeconds;

                var reply = ExtractContent(resp);
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                // Eval-verify: reply must be on-topic for the question.
                AssertOnTopic(turns[turn], reply, evalTerms[turn]);
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                var metrics = TryExtractTurnMetrics(resp);
                var loadMs = metrics?.ModelLoadMs ?? -1;
                var (pt, ct) = ExtractUsage(resp);
                Console.WriteLine(
                    $"Smoke_DenseMultiturnTiming: turn={turn + 1} duration_ms={sw.Elapsed.TotalMilliseconds:F0} " +
                    $"prompt_tokens={pt} completion_tokens={ct} chars={reply.Length} " +
                    $"model_load_ms={loadMs} restore_slot_ms={metrics?.RestoreSlotMs ?? -1} " +
                    $"prompt_ms={metrics?.PromptMs ?? -1} decode_ms={metrics?.DecodeMs ?? -1} " +
                    $"n_past={metrics?.NPast ?? -1}");

                if (turn == 0)
                {
                    // Turn 1 = COLD baseline: model load is expected here.
                    baselineWarmSec = Math.Max(0, durationSec - (metrics?.ModelLoadMs ?? 0) / 1000.0);
                    turn1ModelLoadMs = metrics?.ModelLoadMs;
                    continue;
                }

                // Turn 2: WARM slot expected, 0 expected state transitions.
                var budgetSec = baselineWarmSec!.Value + 10.0;

                // (a) DIRECT: an unexpected reload on a warm turn is the #470
                //     continuation bug. Skip only if hydra_metrics is absent.
                if (metrics is not null)
                {
                    Assert.True(loadMs == 0,
                        $"Turn {turn + 1}: unexpected model reload — model_load_ms={loadMs:F0} (expected 0 on a warm slot; " +
                        $"turn 1 baseline model_load_ms={turn1ModelLoadMs:F0}). KV session was not restored.");
                }
                else
                {
                    Console.WriteLine(
                        $"Smoke_DenseMultiturnTiming: turn={turn + 1} hydra_metrics missing — direct reload check " +
                        "skipped; wall-clock timing budget still enforced");
                }

                // (b) TIMING BUDGET: warm turn must fit baseline_warm + 10s.
                Assert.True(durationSec <= budgetSec,
                    $"Turn {turn + 1}: duration {durationSec:F1}s exceeds budget {budgetSec:F1}s " +
                    $"(baseline_warm={baselineWarmSec:F1}s, model_load_ms={loadMs:F0}) — " +
                    "reload, slow restore, or full re-prefill regression");
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    private async Task<JsonElement> SendCompletionDense(
        string model, string sessionId, List<Dictionary<string, object?>> messages, int timeoutSec = 600)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = MaxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
