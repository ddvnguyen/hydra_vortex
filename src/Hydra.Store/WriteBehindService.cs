using System.Diagnostics;

namespace Hydra.Store;

public sealed class WriteBehindService
{
    private readonly StoreConfig _config;
    private readonly StoreMetadata _metadata;
    private readonly ChunkStore _chunkStore;
    private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<WriteBehindService>();
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private const int BatchSize = 1000;

    public WriteBehindService(StoreConfig config, StoreMetadata metadata, ChunkStore chunkStore)
    {
        _config = config;
        _metadata = metadata;
        _chunkStore = chunkStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var backupChunksDir = new DirectoryInfo(Path.Combine(_config.BackupDir, "chunks"));
        if (!backupChunksDir.Exists)
            backupChunksDir.Create();

        _log.Information(
            "Write-behind service started, interval={Interval}s, backup={BackupDir}",
            Interval.TotalSeconds, backupChunksDir.FullName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
                await FlushUnbackedAsync(backupChunksDir, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Write-behind cycle failed");
            }
        }
    }

    internal async Task FlushUnbackedAsync(DirectoryInfo backupChunksDir, CancellationToken ct)
    {
        var unbacked = await _metadata.GetUnbackedChunksAsync(BatchSize, ct);
        if (unbacked.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var copied = 0;
        long bytesCopied = 0;

        foreach (var (hash, size) in unbacked)
        {
            ct.ThrowIfCancellationRequested();

            var srcPath = Path.Combine(_chunkStore.ChunksDirectory.FullName, hash);
            var dstPath = Path.Combine(backupChunksDir.FullName, hash);

            if (!File.Exists(srcPath))
                continue;

            try
            {
                File.Copy(srcPath, dstPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(dstPath))
            {
                await _metadata.MarkBackedUpAsync(hash, dstPath, ct);
                continue;
            }

            await _metadata.MarkBackedUpAsync(hash, dstPath, ct);
            copied++;
            bytesCopied += size;
        }

        sw.Stop();
        if (copied > 0)
        {
            _log.Information(
                "Write-behind: copied {Count} chunks ({Bytes:F2} MB) in {Elapsed}ms",
                copied, bytesCopied / 1_048_576.0, sw.ElapsedMilliseconds);
        }
    }
}
