using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Epic #470 live-rig coverage for the COMBINED Dense 27B model
/// (dense-27b-combined, layer-split RTX 5060 Ti + RTX 3060) and for dynamic
/// model swap (inline PREFILL reload).
///
/// The rig normally runs the moe profile with Qwopus3.6-35B-A3B resident.
/// Requesting dense-27b-combined triggers a cold_atomic_prefill_swap
/// (60-120s reload to qwen3.6-27B-coder per preset-rtx5060ti.ini), so these
/// tests use generous per-call CancellationTokenSources. Verifies:
///   1. dense-27b-combined serves a completion over the COMBINED pair
///   2. moe → dense → moe swap cycle completes with the session intact
///   3. multi-turn warm-slot verification on the COMBINED pair: turns 2+ must
///      not reload the model (model_load_ms == 0) and must fit a
///      self-calibrating timing budget (baseline_warm + 10s per expected
///      state transition) — #470 FIX-3
///
/// Requires live stack: Coordinator → Workers → llama-engines → Store.
/// Live-rig tier — only runs via workflow_dispatch, not PR-gating.
/// </summary>
[Collection("LiveRig")]
public sealed class CombinedDenseTests : IClassFixture<LiveRigFixture>
{
    private const string DenseModel = "dense-27b-combined";
    private const string MoeModel = "moe-35b-solo";

    private const string SystemPrompt =
        "You are an expert software engineer specialising in distributed systems and GPU inference. " +
        "Provide concise, accurate answers.";

    private readonly LiveRigFixture _fx;

    public CombinedDenseTests(LiveRigFixture fx) => _fx = fx;

    private static string MakeSessionId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// POST /v1/chat/completions with a routing-identity `model` field.
    /// `model` selects the model per models.json (e.g. dense-27b-combined);
    /// requesting a non-resident model triggers the inline PREFILL swap.
    /// </summary>
    private async Task<JsonElement> SendCompletion(
        string model,
        string sessionId,
        List<Dictionary<string, object?>> messages,
        int maxTokens = 4096,
        int timeoutSec = 600)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
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

    /// <summary>
    /// Eval-verify (#596 relevance criterion): the model's reply must be
    /// ON-TOPIC for the question asked. Each group lists accepted keywords;
    /// the reply must contain at least one keyword from EVERY group
    /// (case-insensitive). A non-empty reply that ignores the question is a
    /// model/stack failure.
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

    /// <summary>
    /// Dense 27B COMBINED single completion. The first request may pay the
    /// cold_atomic_prefill_swap reload cost (~60-120s), so the call CTS is
    /// generous (600s — covers swap + 4K-token thinking-heavy decode; the
    /// 300s CTS previously fired mid-generation, run 31370319546) while the
    /// test itself is capped at 900s.
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task Dense27bCombinedCompletion()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("dense-combined");
        Console.WriteLine($"Dense27bCombinedCompletion: model={DenseModel} session={sessionId}");
        try
        {
            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt },
                new() { ["role"] = "user", ["content"] = "Explain in 2 sentences how GPU KV cache migration works." },
            };
            var sw = Stopwatch.StartNew();
            var resp = await SendCompletion(DenseModel, sessionId, messages, maxTokens: 4096);
            sw.Stop();
            Console.WriteLine($"Dense27bCombinedCompletion: elapsed={sw.Elapsed.TotalSeconds:F1}s");

            var choices = resp.GetProperty("choices");
            Assert.True(choices.GetArrayLength() > 0, "No choices in dense-27b-combined response");
            var content = ExtractContent(resp);
            Assert.False(string.IsNullOrEmpty(content),
                "dense-27b-combined returned empty content");
            var finishReason = choices[0].GetProperty("finish_reason").GetString();
            Assert.True(finishReason is "stop" or "length",
                $"Unexpected finish_reason={finishReason}");
            Console.WriteLine($"Dense27bCombinedCompletion: finish_reason={finishReason} chars={content.Length}");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Dynamic model swap: one session crossing moe → dense → moe. The two
    /// swap hops each pay the inline PREFILL reload (~60-120s), so every
    /// request uses a 300s CTS. Success = the coordinator survived a full
    /// swap cycle and the session stayed usable across model changes.
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task DynamicModelSwap_MoeToDenseAndBack()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("dense-swap");
        Console.WriteLine($"DynamicModelSwap: session={sessionId}");
        try
        {
            var history = new List<Dictionary<string, object?>>();

            // Phase A: resident MoE model — fast path.
            await DoPhase("A", MoeModel, sessionId, history,
                "How does expert-parallel routing work in a MoE model? One sentence.");
            // Phase B: swap to dense-27b-combined on the SAME session.
            await DoPhase("B", DenseModel, sessionId, history,
                "How does layer-split execution divide a model across two GPUs? One sentence.");
            // Phase C: swap back to moe-35b-solo, same session again.
            await DoPhase("C", MoeModel, sessionId, history,
                "Summarize the previous two answers in one sentence.");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    private async Task DoPhase(
        string phase,
        string model,
        string sessionId,
        List<Dictionary<string, object?>> history,
        string userMessage)
    {
        var messages = new List<Dictionary<string, object?>>(history)
        {
            new() { ["role"] = "user", ["content"] = userMessage },
        };
        var sw = Stopwatch.StartNew();
        var resp = await SendCompletion(model, sessionId, messages, maxTokens: 4096);
        sw.Stop();
        var content = ExtractContent(resp);
        Console.WriteLine($"DynamicModelSwap: phase={phase} model={model} elapsed={sw.Elapsed.TotalSeconds:F1}s chars={content.Length}");
        Assert.False(string.IsNullOrEmpty(content),
            $"Phase {phase} (model={model}) produced empty reply after swap");
        history.Add(new() { ["role"] = "user", ["content"] = userMessage });
        history.Add(new() { ["role"] = "assistant", ["content"] = content });
    }

    /// <summary>
    /// Multi-turn timing-budget verification on the COMBINED pair (epic #470,
    /// FIX-3). All turns run on dense-27b-combined with the session
    /// accumulating history. State expectations:
    ///   Turn 1 → COLD + model load EXPECTED (baseline_loaded state).
    ///   Turns 2+ → WARM slot expected, NO model swap, expected state
    ///   transitions = 0.
    /// Assertions (timing-based, per the FIX-3 directive — n_past is
    /// informational only):
    ///   (a) DIRECT: turns 2+ must report hydra_metrics.model_load_ms == 0 —
    ///       an unexpected reload (the ~68s cold_atomic swap on continuation)
    ///       fails immediately.
    ///   (b) TIMING BUDGET: baseline_warm = turn1_duration − turn1_model_load_ms
    ///       (self-calibrating, derived inside this run). Turns 2+ budget =
    ///       baseline_warm + 10s × expected_transitions(=0); a warm turn
    ///       exceeding its budget is a reload / slow-restore / prefill
    ///       regression.
    /// Every turn logs a metric table row (duration, model_load_ms,
    /// restore_slot_ms, prompt_ms, decode_ms, n_past) so each run emits the
    /// baseline metric per state.
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task Dense27bMultiturn()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("dense-mt");
        Console.WriteLine($"Dense27bMultiturn: model={DenseModel} session={sessionId}");
        var history = new List<Dictionary<string, object?>>();

        // Self-calibrating baseline: the non-load portion of the cold turn 1
        // duration. Turns 2+ must fit baseline_warm + 10s × expected
        // transitions (0 for a warm slot with no model swap).
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
            // Eval-verify per turn: the reply must be on-topic for the
            // question (KV cache knowledge) — not just non-empty.
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
                    new() { ["role"] = "user", ["content"] = turns[turn] },
                };
                var sw = Stopwatch.StartNew();
                var resp = await SendCompletion(DenseModel, sessionId, messages, maxTokens: 4096);
                sw.Stop();
                var durationSec = sw.Elapsed.TotalSeconds;

                var reply = ExtractContent(resp);
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                // Eval-verify: reply must answer the question, not drift.
                AssertOnTopic(turns[turn], reply, evalTerms[turn]);
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                var metrics = TryExtractTurnMetrics(resp);
                var loadMs = metrics?.ModelLoadMs ?? -1;   // -1 = hydra_metrics absent
                var restoreMs = metrics?.RestoreSlotMs ?? -1;
                var promptMs = metrics?.PromptMs ?? -1;
                var decodeMs = metrics?.DecodeMs ?? -1;
                var nPast = metrics?.NPast ?? -1;

                // Per-turn metric table — emitted before assertions so every
                // run (pass or fail) records the baseline metric per state.
                Console.WriteLine(
                    $"Dense27bMultiturn: turn={turn + 1} duration_ms={sw.Elapsed.TotalMilliseconds:F0} " +
                    $"model_load_ms={loadMs} restore_slot_ms={restoreMs} prompt_ms={promptMs} " +
                    $"decode_ms={decodeMs} n_past={nPast} chars={reply.Length}");

                if (turn == 0)
                {
                    // Turn 1 = COLD baseline: model load is expected here.
                    // baseline_warm is the non-load portion of this turn,
                    // derived in-run so no external calibration is needed.
                    baselineWarmSec = Math.Max(0, durationSec - (metrics?.ModelLoadMs ?? 0) / 1000.0);
                    turn1ModelLoadMs = metrics?.ModelLoadMs;
                    continue;
                }

                // Turns 2+: WARM slot expected, 0 expected state transitions.
                const int expectedTransitions = 0;
                var budgetSec = baselineWarmSec!.Value + 10.0 * expectedTransitions;

                // (a) DIRECT: an unexpected model reload on a warm turn is the
                //     #470 continuation bug (no session-KV restore → full
                //     reload + re-prefill). Skip only if hydra_metrics is
                //     absent from the response (then (b) still guards).
                if (metrics is not null)
                {
                    Assert.True(loadMs == 0,
                        $"Turn {turn + 1}: unexpected model reload — model_load_ms={loadMs:F0} (expected 0 on a warm slot; " +
                        $"turn 1 baseline model_load_ms={turn1ModelLoadMs:F0}). KV session was not restored.");
                }
                else
                {
                    Console.WriteLine(
                        $"Dense27bMultiturn: turn={turn + 1} hydra_metrics missing — direct reload check skipped; " +
                        "wall-clock timing budget still enforced");
                }

                // (b) TIMING BUDGET: warm turn must fit baseline_warm plus 10s
                //     per expected state transition (0). A reload (~68s), a
                //     slow restore, or a full re-prefill blows this.
                Assert.True(durationSec <= budgetSec,
                    $"Turn {turn + 1}: duration {durationSec:F1}s exceeds budget {budgetSec:F1}s " +
                    $"(baseline_warm={baselineWarmSec:F1}s, expected_transitions={expectedTransitions}, " +
                    $"model_load_ms={loadMs:F0}) — reload, slow restore, or full re-prefill regression");
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Extract the per-turn timing metrics the COMBINED engine attaches to the
    /// response body as hydra_metrics (decode path: model_load_ms,
    /// restore_slot_ms, prompt_ms, decode_ms, n_past). Returns null when the
    /// response lacks hydra_metrics (e.g. HTTP proxy fallback) — callers then
    /// fall back to the wall-clock budget.
    /// </summary>
    private static TurnMetrics? TryExtractTurnMetrics(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("hydra_metrics", out var hm) || hm.ValueKind != JsonValueKind.Object)
            return null;
        double GetNum(string name) =>
            hm.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
        return new TurnMetrics(
            ModelLoadMs: GetNum("model_load_ms"),
            RestoreSlotMs: GetNum("restore_slot_ms"),
            PromptMs: GetNum("prompt_ms"),
            DecodeMs: GetNum("decode_ms"),
            NPast: GetNum("n_past"));
    }

    private readonly record struct TurnMetrics(
        double ModelLoadMs, double RestoreSlotMs, double PromptMs, double DecodeMs, double NPast);
}
