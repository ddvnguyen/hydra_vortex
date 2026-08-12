using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Tests.LiveRig.Ordering;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_mix_precision_p_d_system.py.
///
/// Cross-model KV safety — system test for the M-Perf.9 #289 wiring and the
/// epic #470 merged-decode contract. Exercises
/// WorkerSchedulerService.RestoreKvAsync via the live Coordinator HTTP API.
/// Verifies:
///   1. Merged same-model restore (engine advertises merged_decode): succeeds,
///      and the coordinator-side cross-model guard does NOT fire — STATE_PUT
///      is skipped by design (restore_kv_merged_skip_state_put); the engine
///      validates model identity itself (Gate A) before restoring KV.
///   2. Metric exposure: Prometheus endpoint exposes cross-model guard counters
///
/// Requires live stack: Coordinator :9000, llama-server(s), Store.
/// </summary>
[Collection("LiveRig")]
public sealed class MixPrecisionPdTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    private const string SystemPrompt = "You are a helpful assistant. Answer the user's question concisely.";
    private const string UserPrompt1 = "What is 2 + 2? Reply with only the number.";
    private const string UserPrompt2 = "Multiply that by 3. Reply with only the number.";

    private static readonly string[] CounterNames =
    [
        "hydra_cross_model_kv_proceeded_total",
        "hydra_cross_model_kv_skipped_total",
        "hydra_cross_model_kv_warned_total",
        "hydra_cross_model_kv_aborted_total",
    ];

    public MixPrecisionPdTests(LiveRigFixture fx) => _fx = fx;

    private string MakeSessionId() => $"system-cross-model-{Guid.NewGuid():N}"[..20];

    private static List<Dictionary<string, object?>> MakeMessages(string system, string user) =>
    [
        new() { ["role"] = "system", ["content"] = system },
        new() { ["role"] = "user", ["content"] = user },
    ];

    private static List<Dictionary<string, object?>> MakeFollowupMessages(
        string system, List<Dictionary<string, object?>> history, string user)
    {
        var msgs = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        msgs.AddRange(history);
        msgs.Add(new() { ["role"] = "user", ["content"] = user });
        return msgs;
    }

    private async Task<JsonElement> DoCompletion(
        List<Dictionary<string, object?>> messages,
        string sessionId,
        int maxTokens = 32)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ExtractContent(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "";
        var msg = choices[0].GetProperty("message");
        return (HttpHelpers.GetOutputText(msg)).Trim();
    }

    private async Task<double> GetCounter(string name, Dictionary<string, string>? labels = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var resp = await HttpHelpers.Client.GetAsync(_fx.CoordMetricsUrl, cts.Token);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var samples = HttpHelpers.ParsePromLines(body);
        return HttpHelpers.SumCounter(samples, name, labels);
    }

    /// <summary>
    /// Epic #470 merged-decode contract: a same-model restore on a
    /// merged_decode engine never runs the coordinator-side cross-model guard.
    ///
    /// RestoreKvAsync (WorkerSchedulerService.cs:2902) detects CapMergedDecode
    /// and skips the blind STATE_PUT entirely (restore_kv_merged_skip_state_put
    /// at WorkerSchedulerService.cs:2908) — the KV blob rides inside the framed
    /// DECODE 0x43 RPC instead, where the engine validates model identity
    /// (Gate A) BEFORE restoring KV. Consequently none of the
    /// hydra_cross_model_kv_{proceeded,skipped,warned,aborted}_total counters
    /// move on merged restores — they only increment on the legacy non-merged
    /// STATE_PUT / cold-slot paths (WorkerSchedulerService.cs:2984-3006).
    ///
    /// This test replaces the pre-#470 assertion ("Proceed counter increases
    /// on a same-model migration restore") that could no longer fire once the
    /// epic's merged-decode path took over (run #31405080406: Before=3,
    /// After=3). It was a STALE TEST, not a prod bug.
    ///
    /// New contract assertions:
    ///   1. Turn 2 (evicted same-model session) succeeds with content. With
    ///      Gate A enforced, success requires the merged DECODE's model alias
    ///      to resolve to the resident model — the KvModelAlias fallback
    ///      (#609, WorkerSchedulerService.cs:2331-2339: KV-manifest alias beats
    ///      the request routing identity for model-agnostic sessions; alias vs
    ///      GGUF-filename match tolerated). A broken alias fallback yields a
    ///      name=0 Gate-A reject → Valid=false → the request ABORTS
    ///      (WorkerSchedulerService.cs:3430-3440) → this assertion fails.
    ///   2. None of the four cross-model guard counters move during turn 2 —
    ///      proving STATE_PUT was skipped (merged path taken). A regression
    ///      that re-enables the legacy STATE_PUT on merged engines fires the
    ///      guard (Proceed for same-model) and fails this test.
    /// </summary>
    /// <summary>
    /// P/D cross-model KV restore contract tests — group 2 (orders 28-30,
    /// moe-35b-pd), one intentional model swap into the P/D split (rtx Mini
    /// prefill + p100 balanced decode). Global order via [TestOrder] +
    /// assembly-wide TestCaseOrderer (Ordering/, #470).
    /// </summary>
    [TestOrder(28)]
    [SkippableFact]
    public async Task CrossModelMergedRestoreSameModelNoGuardFire()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            // Baseline: no cross-model guard counter may move during this test —
            // the merged-decode path skips the coordinator-side guard by design.
            var baseline = new Dictionary<string, double>();
            foreach (var name in CounterNames)
                baseline[name] = await GetCounter(name);

            // Turn 1: initial request — cold route, prefill, save KV to store
            var resp1 = await DoCompletion(MakeMessages(SystemPrompt, UserPrompt1), sessionId, maxTokens: 8);
            var content1 = ExtractContent(resp1);
            Assert.False(string.IsNullOrEmpty(content1), $"Turn 1 empty");

            // Evict the session so turn 2 must go through migration → RestoreKvAsync
            using var delCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var delResp = await HttpHelpers.Client.DeleteAsync($"{_fx.CoordUrl}/sessions/{sessionId}", delCts.Token);
            Assert.True(delResp.IsSuccessStatusCode, $"Eviction failed: {await delResp.Content.ReadAsStringAsync()}");
            var delBody = await delResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(delBody.GetProperty("evicted").GetBoolean());

            // Turn 2: same model, same worker, after eviction → migration route →
            // RestoreKvAsync → merged path (CapMergedDecode) → STATE_PUT skipped,
            // KV carried in the framed DECODE 0x43 RPC.
            var history = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = UserPrompt1 },
                new() { ["role"] = "assistant", ["content"] = content1 },
            };
            var resp2 = await DoCompletion(
                MakeFollowupMessages(SystemPrompt, history, UserPrompt2), sessionId, maxTokens: 8);
            var content2 = ExtractContent(resp2);

            // Assertion 1: the merged restore succeeded — Gate A accepted the KV.
            // A wrong/absent model alias (pre-#609: request routing identity only)
            // would be rejected by the engine's Gate-A name fallback → Valid=false
            // → request abort → this assertion fails (no HTTP-proxy fallback on the
            // gate-reject path, WorkerSchedulerService.cs:3430-3440).
            Assert.False(string.IsNullOrEmpty(content2),
                $"Turn 2 empty — merged restore was rejected by the engine's Gate A " +
                $"(model identity / alias mismatch) or produced no decode");

            // Assertion 2: the merged path skips the coordinator-side cross-model
            // guard entirely — none of its counters may move during this test.
            // Any movement means STATE_PUT ran (legacy path), i.e. the merged-decode
            // contract regressed.
            foreach (var name in CounterNames)
            {
                var after = await GetCounter(name);
                Assert.True(after == baseline[name],
                    $"hydra_cross_model_kv counter {name} moved during a merged same-model restore — " +
                    $"STATE_PUT / CrossModelGuard ran on a merged_decode engine. " +
                    $"Before={baseline[name]}, After={after}.");
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [TestOrder(29)]
    [SkippableFact]
    public async Task CrossModelMetricExposed()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            // Make at least one request so the metric series are emitted
            var resp = await DoCompletion(MakeMessages(SystemPrompt, "Say 'ok'."), sessionId, maxTokens: 4);
            Assert.True(resp.TryGetProperty("choices", out _));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var m = await HttpHelpers.Client.GetAsync(_fx.CoordMetricsUrl, cts.Token);
            Assert.True(m.IsSuccessStatusCode);
            var body = await m.Content.ReadAsStringAsync();

            foreach (var name in CounterNames)
            {
                Assert.Contains($"# HELP {name} ", body);
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
