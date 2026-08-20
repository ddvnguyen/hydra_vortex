using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Pure-logic unit tests for <see cref="HydraLogScraper"/> parsing methods.
/// These do NOT require a live rig — they test parsing of canned log lines.
/// </summary>
public class HydraLogScraperParsingTests
{
    [Fact]
    public void ParseRequestTimeline_ValidLine_ExtractsAllFields()
    {
        const string line = "[2026-08-03T12:34:56.789Z] event=request_timeline tokens_out=150 decode_ms=750.0 queue_wait_ms=0 restore_kv_ms=0 route_type=affinity node=rtx status=done slot=0 model=moe-35b-solo";

        var evt = HydraLogScraper.ParseRequestTimeline(line);

        Assert.NotNull(evt);
        Assert.Equal("rtx", evt.Node);
        Assert.Equal("affinity", evt.RouteType);
        Assert.Equal(150, evt.TokensOut);
        Assert.Equal(750f, evt.DecodeMs);
        Assert.Equal(0f, evt.QueueWaitMs);
        Assert.Equal(0f, evt.RestoreKvMs);
        Assert.Equal("done", evt.Status);
        Assert.Equal("0", evt.Slot);
        Assert.Equal("moe-35b-solo", evt.Model);
        Assert.Equal(line, evt.RawLine);
    }

    [Fact]
    public void ParseRequestTimeline_ThroughputCalculation_MatchesDocSnippet()
    {
        const string line = "[2026-08-03T12:34:56.789Z] event=request_timeline tokens_out=200 decode_ms=1000.0 queue_wait_ms=0 route_type=solo node=rtx5060ti status=done";

        var evt = HydraLogScraper.ParseRequestTimeline(line);
        Assert.NotNull(evt);

        // 200 tok / (1000ms / 1000) = 200 tok/s — matches RTX 5060 Ti baseline
        var throughput = HydraLogScraper.ComputeThroughput(evt);
        Assert.Equal(200f, throughput, 1f);
    }

    [Fact]
    public void ParseRequestTimeline_MissingTokensOut_ReturnsNull()
    {
        const string line = "[2026-08-03T12:34:56.789Z] event=request_timeline decode_ms=750.0 status=done";

        var evt = HydraLogScraper.ParseRequestTimeline(line);
        Assert.Null(evt);
    }

    [Fact]
    public void ParseRequestTimeline_NonTimelineLine_ReturnsNull()
    {
        const string line = "some random log line without the event marker";

        var evt = HydraLogScraper.ParseRequestTimeline(line);
        Assert.Null(evt);
    }

    [Fact]
    public void ParseKvReuse_NCommonLine_ExtractsEventType()
    {
        const string line = "[2026-08-03T12:34:56.789Z] #PD-TRACE N_COMMON=42 prefix_match slot=0";

        var evt = HydraLogScraper.ParseKvReuse(line);

        Assert.NotNull(evt);
        Assert.Equal("N_COMMON", evt.EventType);
        Assert.Equal(42, evt.NCommon);
        Assert.Equal(line, evt.RawLine);
    }

    [Fact]
    public void ParseKvReuse_NCommonNoValue_ExtractsEventTypeOnly()
    {
        const string line = "[2026-08-03T12:34:56.789Z] N_COMMON fired for slot 1";

        var evt = HydraLogScraper.ParseKvReuse(line);

        Assert.NotNull(evt);
        Assert.Equal("N_COMMON", evt.EventType);
        Assert.Null(evt.NCommon); // no digit match after N_COMMON
    }

    [Fact]
    public void ParseKvReuse_RestoredLogits_ExtractsEventType()
    {
        const string line = "[2026-08-03T12:34:56.789Z] restored logits from KV cache slot=0";

        var evt = HydraLogScraper.ParseKvReuse(line);

        Assert.NotNull(evt);
        Assert.Equal("restored_logits", evt.EventType);
    }

    [Fact]
    public void ParseKvReuse_SlotReleased_ExtractsSlot()
    {
        const string line = "[2026-08-03T12:34:56.789Z] slot 0 released after completion";

        var evt = HydraLogScraper.ParseKvReuse(line);

        Assert.NotNull(evt);
        Assert.Equal("slot_released", evt.EventType);
        Assert.Equal("0", evt.Slot);
    }

    [Fact]
    public void ParseCrashRestart_GgmlAssert_ExtractsEventType()
    {
        const string line = "[2026-08-03T12:34:56.789Z] GGML_ASSERT: CUDA error at ggml_cuda.cu:1234";

        var evt = HydraLogScraper.ParseCrashRestart(line);

        Assert.NotNull(evt);
        Assert.Equal("GGML_ASSERT", evt.EventType);
        Assert.Contains("CUDA error", evt.Details);
    }

    [Fact]
    public void ParseCrashRestart_ExitCode_ExtractsEventType()
    {
        const string line = "[2026-08-03T12:34:56.789Z] process exited with exit_code=134";

        var evt = HydraLogScraper.ParseCrashRestart(line);

        Assert.NotNull(evt);
        Assert.Equal("exit_code", evt.EventType);
    }

    [Fact]
    public void ParseCrashRestart_AttemptingRestart_ExtractsEventType()
    {
        const string line = "[2026-08-03T12:34:56.789Z] attempting restart of llama-engine";

        var evt = HydraLogScraper.ParseCrashRestart(line);

        Assert.NotNull(evt);
        Assert.Equal("attempting_restart", evt.EventType);
    }

    [Fact]
    public void ParseCrashRestart_NoMatch_ReturnsNull()
    {
        const string line = "[2026-08-03T12:34:56.789Z] normal log line nothing wrong";

        var evt = HydraLogScraper.ParseCrashRestart(line);
        Assert.Null(evt);
    }

    [Fact]
    public void ExtractKeyValuePairs_MultiplePairs()
    {
        const string line = "tokens_out=150 decode_ms=750.0 queue_wait_ms=0 route_type=affinity";

        var dict = HydraLogScraper.ExtractKeyValuePairs(line);

        Assert.Equal("150", dict["tokens_out"]);
        Assert.Equal("750.0", dict["decode_ms"]);
        Assert.Equal("0", dict["queue_wait_ms"]);
        Assert.Equal("affinity", dict["route_type"]);
    }

    [Fact]
    public void ExtractKeyValuePairs_EmptyLine_ReturnsEmptyDict()
    {
        var dict = HydraLogScraper.ExtractKeyValuePairs("");
        Assert.Empty(dict);
    }

    [Fact]
    public void ExtractTimestamp_ValidTimestamp_ParsesCorrectly()
    {
        const string line = "[2026-08-03T12:34:56.789Z] some log";

        var ts = HydraLogScraper.ExtractTimestamp(line);

        Assert.Equal(2026, ts.Year);
        Assert.Equal(8, ts.Month);
        Assert.Equal(3, ts.Day);
        Assert.Equal(12, ts.Hour);
        Assert.Equal(34, ts.Minute);
        Assert.Equal(56, ts.Second);
    }

    [Fact]
    public void ExtractTimestamp_NoTimestamp_ReturnsMinValue()
    {
        const string line = "no timestamp here";

        var ts = HydraLogScraper.ExtractTimestamp(line);

        Assert.Equal(DateTimeOffset.MinValue, ts);
    }

    // ── HydraLogScraper: autoroute_resolved parsing (issue #596) ──

    [Fact]
    public void ParseAutoRoute_ValidLine_ExtractsAllFields()
    {
        const string line = "[2026-08-10T12:00:00.000Z] autoroute_resolved Sid=test-hydra-auto-abc123 Model=moe-35b-pd Head=rtx Peer=none Decode=p100 Mode=pd";

        var evt = HydraLogScraper.ParseAutoRoute(line);

        Assert.NotNull(evt);
        Assert.Equal("test-hydra-auto-abc123", evt.Sid);
        Assert.Equal("moe-35b-pd", evt.Model);
        Assert.Equal("rtx", evt.Head);
        Assert.Equal("none", evt.Peer);
        Assert.Equal("p100", evt.Decode);
        Assert.Equal("pd", evt.Mode);
        Assert.Equal(line, evt.RawLine);
    }

    [Fact]
    public void ParseAutoRoute_NonAutoRouteLine_ReturnsNull()
    {
        const string line = "[2026-08-10T12:00:00.000Z] event=request_timeline tokens_out=150 decode_ms=750.0 status=done";

        var evt = HydraLogScraper.ParseAutoRoute(line);

        Assert.Null(evt);
    }

    [Fact]
    public void ParseAutoRoute_MissingSid_ReturnsNull()
    {
        const string line = "[2026-08-10T12:00:00.000Z] autoroute_resolved Model=moe-35b-pd Head=rtx Mode=pd";

        var evt = HydraLogScraper.ParseAutoRoute(line);

        Assert.Null(evt);
    }

    // ── HydraLogScraper: model_routing_check correlation (issue #596) ──

    [Fact]
    public void ExtractModelRoutingSid_MatchesRequestedModel_ReturnsSid()
    {
        // Real coordinator line (Sid is a hash, not the CLI session id).
        const string line = "[2026-08-10T03:30:23Z INF] model_routing_check Sid=sess_9c9a1ea8a0056e898bf6b97c ModelStr=hydra-auto";

        var sid = HydraLogScraper.ExtractModelRoutingSid(line, "hydra-auto");

        Assert.Equal("sess_9c9a1ea8a0056e898bf6b97c", sid);
    }

    [Fact]
    public void ExtractModelRoutingSid_DifferentModel_ReturnsNull()
    {
        const string line = "[2026-08-10T03:30:01Z INF] model_routing_check Sid=sess_44305e36df753b88ab09de42 ModelStr=moe-35b-solo";

        var sid = HydraLogScraper.ExtractModelRoutingSid(line, "hydra-auto");

        Assert.Null(sid);
    }

    [Fact]
    public void ExtractModelRoutingSid_NonRoutingLine_ReturnsNull()
    {
        const string line = "[2026-08-10T03:30:01Z INF] event=request_timeline tokens_out=150 status=done";

        var sid = HydraLogScraper.ExtractModelRoutingSid(line, "hydra-auto");

        Assert.Null(sid);
    }
}
