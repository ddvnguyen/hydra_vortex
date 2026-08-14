using System.Diagnostics;

namespace Hydra.Core;

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

            try
            {
                if (!File.Exists(srcPath))
                    continue;

                File.Copy(srcPath, dstPath, overwrite: false);
                await _metadata.MarkBackedUpAsync(hash, dstPath, ct);
                copied++;
                bytesCopied += size;
            }
            catch (IOException) when (File.Exists(dstPath))
            {
                // A previous cycle copied the chunk but crashed before marking
                // it backed up — the destination already holds the bytes. Record
                // it and move on; guarded so a metadata failure here can't abort
                // the rest of the cycle either.
                try { await _metadata.MarkBackedUpAsync(hash, dstPath, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Warning(ex, "Write-behind: mark-backed-up failed {Hash} — continuing flush cycle", hash); }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // #470 Tier-4: ONE bad file must not abort the whole flush cycle.
                // The backup dir is ntfs3-backed (SATA SSD) where stale files throw
                // UnauthorizedAccessException (EPERM), and a source deleted between
                // the metadata snapshot and the copy throws IOException. Either way
                // the remaining chunks still drain — otherwise the tmpfs backlog
                // grows until the mount fills. The file stays unbacked and is
                // retried on the next cycle.
                _log.Warning(ex, "Write-behind: skipped chunk {Hash} ({Reason}) — continuing flush cycle", hash, ex.GetType().Name);
            }
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
