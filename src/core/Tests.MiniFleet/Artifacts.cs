namespace Tests.MiniFleet;

/// <summary>
/// Artifact supply + evidence emission (brief §Components 1-2).
///
/// Model: download-on-demand, PINNED —
///   https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf
///   sha256 = <see cref="ModelSha256"/>.
/// Env overrides so rig lanes skip downloads:
///   MINIFLEET_MODEL_PATH / MINIFLEET_ENGINE_BIN.
/// CI caches the model under actions/cache keyed by the sha256 (minifleet.yml).
///
/// Evidence: when both HYDRA_SCHEDULER_IMPL passes run, emit legacy-vs-v2 trace
/// JSON pair to tests/minifleet-artifacts/&lt;preset&gt;/&lt;scenario&gt;.json;
/// AC2 additionally commits runs under docs/minifleet/evidence/.
/// </summary>
public static class Artifacts
{
    public const string ModelUrl =
        "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf";

    public const string ModelSha256 =
        "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8";

    // TODO(minifleet): EnsureModelAsync — hf CLI download if absent; verify sha256;
    //   honor MINIFLEET_MODEL_PATH override first.
    // TODO(minifleet): ResolveEngineBinaryAsync — MINIFLEET_ENGINE_BIN override else staged path.
    // TODO(minifleet): WriteTracePairAsync(preset, scenarioId, legacyTrace, v2Trace)
}
