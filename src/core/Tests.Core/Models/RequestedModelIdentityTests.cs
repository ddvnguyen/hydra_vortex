using Hydra.Core.Models;
using Hydra.Core.Services;
using Xunit;

namespace Tests.Core.Models;

/// <summary>
/// #470 canonical identity: RequestedModelIdentity resolves the raw routing
/// key once at ingress into the role-aware GGUF-file aliases every payload
/// builder consumes. The raw routing key (e.g. "dense-27b-combined") is not a
/// key of the engine's --models-preset and must never reach the engine wire.
/// </summary>
public class RequestedModelIdentityTests
{
    /// <summary>Mirror of the production models.json shape: combined maps to
    /// the coder GGUF alias; P/D-split moe-35b-pd maps prefill to mini and
    /// decode to balanced.</summary>
    private static ModelConfigLoader Loader()
    {
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            Models = new Dictionary<string, ModelTemplate>
            {
                ["moe-35b-pd"] = new ModelTemplate
                {
                    PrefillAlias = "qwen3.6-35B-mini",
                    DecodeAlias  = "qwen3.6-35B-balanced",
                },
                ["dense-27b-combined"] = new ModelTemplate
                {
                    PrefillAlias = "qwen3.6-27B-coder",
                    DecodeAlias  = "qwen3.6-27B-coder",
                },
            },
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"]     = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                ["qwen3.6-35B-balanced"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
                ["qwen3.6-27B-coder"]    = "Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            },
        };
        return ModelConfigLoader.Create(config);
    }

    [Fact]
    public void DenseCombined_CombinedTrue_BothRolesResolveToCoderAlias()
    {
        var identity = RequestedModelIdentity.Resolve("dense-27b-combined", Loader());

        Assert.Equal("dense-27b-combined", identity.RoutingKey);
        Assert.True(identity.Combined);
        Assert.Equal("qwen3.6-27B-coder", identity.PrefillAlias);
        Assert.Equal("qwen3.6-27B-coder", identity.DecodeAlias);
    }

    [Fact]
    public void MoePd_CombinedFalse_RoleAwareQuants()
    {
        var identity = RequestedModelIdentity.Resolve("moe-35b-pd", Loader());

        Assert.Equal("moe-35b-pd", identity.RoutingKey);
        Assert.False(identity.Combined);
        // P/D split: prefill runs the mini quant, decode the balanced quant.
        Assert.Equal("qwen3.6-35B-mini", identity.PrefillAlias);
        Assert.Equal("qwen3.6-35B-balanced", identity.DecodeAlias);
    }

    [Fact]
    public void UnknownKey_Passthrough_CombinedFalse()
    {
        var identity = RequestedModelIdentity.Resolve("some-unknown-model", Loader());

        Assert.Equal("some-unknown-model", identity.RoutingKey);
        Assert.False(identity.Combined);
        // Unknown routing key → no template → aliases are the key itself
        // (pre-feature behavior preserved).
        Assert.Equal("some-unknown-model", identity.PrefillAlias);
        Assert.Equal("some-unknown-model", identity.DecodeAlias);
    }

    [Fact]
    public void CombinedMarker_IsCaseInsensitive()
    {
        var identity = RequestedModelIdentity.Resolve("DENSE-27B-COMBINED", Loader());

        Assert.True(identity.Combined);
    }

    [Fact]
    public void NoLoader_Passthrough()
    {
        var identity = RequestedModelIdentity.Resolve("dense-27b-combined", loader: null);

        Assert.Equal("dense-27b-combined", identity.RoutingKey);
        Assert.True(identity.Combined, "the combined marker is derived from the raw key, independent of the loader");
        Assert.Equal("dense-27b-combined", identity.PrefillAlias);
        Assert.Equal("dense-27b-combined", identity.DecodeAlias);
    }

    [Fact]
    public void NullKey_AllNull_CombinedFalse()
    {
        var identity = RequestedModelIdentity.Resolve(null, Loader());

        Assert.Null(identity.RoutingKey);
        Assert.Null(identity.PrefillAlias);
        Assert.Null(identity.DecodeAlias);
        Assert.False(identity.Combined);
    }
}
