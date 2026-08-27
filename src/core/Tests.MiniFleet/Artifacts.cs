using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Tests.MiniFleet;

/// <summary>
/// Artifact supply + evidence emission (brief §Components 1-2).
///
/// Model: download-on-demand, PINNED —
///   <see cref="ModelUrl"/>, sha256 = <see cref="ModelSha256"/>.
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

    public const string ModelFileName = "Qwen3.5-9B-Q4_K_M.gguf";

    /// <summary>Env override: use this exact model file, skip download/verify.</summary>
    public const string ModelPathEnvVar = "MINIFLEET_MODEL_PATH";

    /// <summary>Env override: use this exact engine binary, skip staging.</summary>
    public const string EngineBinEnvVar = "MINIFLEET_ENGINE_BIN";

    /// <summary>Local model cache root (CI actions/cache points here too).</summary>
    public static string CacheRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "minifleet", "models");

    /// <summary>
    /// Resolution order (brief §Components 2):
    /// 1. MINIFLEET_MODEL_PATH env override — used as-is when the file exists.
    /// 2. Cache hit <see cref="CacheRoot"/>/<see cref="ModelFileName"/> — sha256
    ///    verified (cheap; guards a truncated CI cache) then returned.
    /// 3. Download to <c>&lt;final&gt;.partial</c> with streaming SHA256, atomic
    ///    rename after verification, hf CLI as fallback when the direct GET fails.
    /// </summary>
    public static async Task<FileInfo> EnsureModelAsync(CancellationToken ct = default)
    {
        var overridePath = Environment.GetEnvironmentVariable(ModelPathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new FileNotFoundException(
                    $"{ModelPathEnvVar} is set but the file does not exist: {overridePath}");
            }
            return new FileInfo(overridePath);
        }

        Directory.CreateDirectory(CacheRoot);
        var finalPath = Path.Combine(CacheRoot, ModelFileName);
        if (File.Exists(finalPath))
        {
            await VerifySha256Async(finalPath, ct).ConfigureAwait(false);
            return new FileInfo(finalPath);
        }

        var partialPath = finalPath + ".partial";
        try
        {
            await DownloadToAsync(ModelUrl, partialPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Direct CDN GET failed (rate limits, redirects, proxies) — brief
            // mandates the hf CLI as the fallback download path.
            await HfCliDownloadAsync(partialPath, ct).ConfigureAwait(false);
        }

        await VerifySha256Async(partialPath, ct).ConfigureAwait(false);
        File.Move(partialPath, finalPath, overwrite: false);
        return new FileInfo(finalPath);
    }

    /// <summary>MINIFLEET_ENGINE_BIN override, else the staged ~/hydra-min-test path
    /// (gpu-gpu-shared lane) — caller falls back to per-preset staging when null.</summary>
    public static string? ResolveEngineBinary()
    {
        var bin = Environment.GetEnvironmentVariable(EngineBinEnvVar);
        return string.IsNullOrWhiteSpace(bin) ? null : bin;
    }

    /// <summary>Streams <paramref name="path"/> through SHA256 and throws on mismatch
    /// with the pinned digest. 64 KB buffer: no multi-GB byte[] allocations.</summary>
    private static async Task VerifySha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var digestBytes = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        var digest = Convert.ToHexString(digestBytes).ToLowerInvariant();
        if (digest != ModelSha256)
        {
            throw new InvalidOperationException(
                $"Model artifact digest mismatch for {path}: expected {ModelSha256}, got {digest}. " +
                "Delete the cached file and retry (a stale/partial cache is the usual cause).");
        }
    }

    /// <summary>GET <see cref="ModelUrl"/> while hashing+writing in one pass —
    /// no temp re-read. Caller verifies the digest before the atomic rename.</summary>
    private static async Task DownloadToAsync(string url, string destination, CancellationToken ct)
    {
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        await httpStream.CopyToAsync(output, bufferSize: 64 * 1024, ct).ConfigureAwait(false);
    }

    /// <summary>hf CLI fallback: `hf download` (new name) or `huggingface-cli download`
    /// (legacy name); the snapshot file is copied to <paramref name="destination"/>.</summary>
    private static async Task HfCliDownloadAsync(string destination, CancellationToken ct)
    {
        var snapshot = await RunHfDownloadAsync("hf", ct)
            .OrAwait(() => RunHfDownloadAsync("huggingface-cli", ct)).ConfigureAwait(false);
        File.Copy(snapshot.SnapshotFilePath, destination, overwrite: true);
    }

    private static async Task<(string SnapshotFilePath, string Tool)> RunHfDownloadAsync(
        string toolName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolName,
            ArgumentList =
            {
                "download",
                "unsloth/Qwen3.5-9B-GGUF",
                ModelFileName,
                "--repo-type", "model",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch {toolName}.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{toolName} download failed (exit {process.ExitCode}): {stderr}");
        }

        // `hf download` prints the local snapshot path of the requested file.
        var snapshotPath = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.EndsWith(ModelFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"{toolName} produced no snapshot path for {ModelFileName}. stdout: {stdout}");
        return (snapshotPath, toolName);
    }

    /// <summary>Legacy-vs-v2 trace pair emission (brief §Components 1, A/B hooks):
    /// tests/minifleet-artifacts/&lt;preset&gt;/&lt;scenario&gt;.json + -v2.json.</summary>
    public static async Task WriteTracePairAsync(
        string preset, string scenarioId, string legacyTraceJson, string? v2TraceJson)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(),
            "tests", "minifleet-artifacts", preset);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"{scenarioId}.json"), legacyTraceJson).ConfigureAwait(false);
        if (v2TraceJson is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, $"{scenarioId}-v2.json"), v2TraceJson).ConfigureAwait(false);
        }
    }
}

internal static class AwaitFallbackExtensions
{
    /// <summary>Try the primary awaitable; on ANY failure run the fallback instead.
    /// Used so an absent/legacy-named hf CLI transparently falls back.</summary>
    public static async Task<T> OrAwait<T>(this Task<T> primary, Func<Task<T>> fallback)
    {
        try
        {
            return await primary.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return await fallback().ConfigureAwait(false);
        }
    }
}
