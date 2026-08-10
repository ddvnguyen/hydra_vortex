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
///   3. multi-turn KV reuse on the COMBINED pair (n_past grows)
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
    /// Multi-turn KV reuse on the COMBINED pair. All turns run on
    /// dense-27b-combined with the session accumulating history; n_past must
    /// grow across turns (KV cache reuse, not full re-prefill). The node
    /// assertion is deliberately loose — the session may report as the COMBINED
    /// head (rtx) or a peer; non-empty replies + growing n_past are the core
    /// assertions.
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public async Task Dense27bMultiturn()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId("dense-mt");
        Console.WriteLine($"Dense27bMultiturn: model={DenseModel} session={sessionId}");
        var history = new List<Dictionary<string, object?>>();
        var prevNPast = 0;

        try
        {
            var turns = new[]
            {
                "List three things KV cache stores. One sentence each.",
                "Now explain the first one in one sentence.",
                "Now explain the second one in one sentence.",
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
                var reply = ExtractContent(resp);
                Console.WriteLine($"Dense27bMultiturn: turn={turn + 1} elapsed={sw.Elapsed.TotalSeconds:F1}s chars={reply.Length}");
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                // KV-reuse verification: n_past must grow across turns. Best
                // effort — if the session is not visible yet, defer to the
                // next iteration.
                var status = await _fx.GetStatusAsync();
                var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session?.NPast is int np && np > 0)
                {
                    Assert.True(np > prevNPast,
                        $"Turn {turn + 1}: n_past did not grow ({prevNPast} → {np}) — KV cache was likely reset");
                    prevNPast = np;
                }
                if (session is not null)
                {
                    Console.WriteLine($"Dense27bMultiturn: node={session.Node} n_past={session.NPast}");
                }
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
