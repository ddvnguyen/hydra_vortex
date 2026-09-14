using Hydra.Core.Models;
using Hydra.Core.Services.SchedulerV2;

namespace Tests.Core.SchedulerV2Tests;

public sealed class RequestClassifierTests
{
    private static readonly CoordinatorConfig Config = new() { AtomicThreshold = 2048 };
    private static readonly ChatRequest Req = ChatRequest.FromSubmit(
        new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 100 },
        new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
        "sess", estimatedTokens: 100, maxTokens: 100, prefixHash: null, systemPromptTokens: 0);

    [Fact]
    public void Small_Request_Is_Atomic()
    {
        var classifier = new RequestClassifier();
        Assert.Equal(RequestType.Atomic, classifier.Classify(Req, Config, hasWarmSession: false));
    }

    [Fact]
    public void Large_Request_Is_Prefill()
    {
        var classifier = new RequestClassifier();
        var big = Req with { EstimatedTokens = 100_000 };
        Assert.Equal(RequestType.Prefill, classifier.Classify(big, Config, hasWarmSession: false));
    }

    [Fact]
    public void Warm_Session_Is_Solo()
    {
        var classifier = new RequestClassifier();
        Assert.Equal(RequestType.Solo, classifier.Classify(Req, Config, hasWarmSession: true));
    }

    [Fact]
    public void Priority_Ladder_Matches_Legacy()
    {
        var classifier = new RequestClassifier();
        var decode = classifier.ComputePriority(RequestType.Decode);
        var solo = classifier.ComputePriority(RequestType.Solo);
        var atomic = classifier.ComputePriority(RequestType.Atomic);
        var prefill = classifier.ComputePriority(RequestType.Prefill);

        Assert.True(decode < solo, "decode must outrank solo");
        Assert.True(solo < atomic, "solo must outrank atomic");
        Assert.True(atomic < prefill, "atomic must outrank prefill");
    }
}
