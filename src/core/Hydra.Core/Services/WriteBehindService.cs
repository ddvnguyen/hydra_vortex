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
        var backupRootDir = new DirectoryInfo(_config.BackupDir);
        if (!backupRootDir.Exists)
            backupRootDir.Create();

        _log.Information(
            "Write-behind service started, interval={Interval}s, backup={BackupDir}",
            Interval.TotalSeconds, backupRootDir.FullName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
                await FlushUnbackedAsync(backupChunksDir, ct);
                await FlushUnbackedKvAsync(backupRootDir, ct);
                await FreeBackedUpFromRamAsync(ct);
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

    /// <summary>
    /// Drains per-session <c>sess_*.kv</c> manifest blobs from the StoreDir root
    /// (tmpfs RAM) to the SSD backup ROOT (not <c>chunks/</c>), marking each one
    /// backed up in PG (<c>kv_manifests</c>) — the SSD-durable tier for the .kv
    /// side (#470). Previously .kv blobs lived only in the 40 GB tmpfs and were
    /// never drained, so the mount filled and prefill RPCs entered a busy-retry
    /// loop. Mirrors <see cref="FlushUnbackedAsync"/>'s per-file resilience: one
    /// bad file (already-exists race, missing source, ntfs3 EPERM) never aborts
    /// the cycle.
    /// </summary>
    internal async Task FlushUnbackedKvAsync(DirectoryInfo backupRootDir, CancellationToken ct)
    {
        var unbacked = await _metadata.GetUnbackedKvAsync(_config.StoreDirectory, BatchSize, ct);
        if (unbacked.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var copied = 0;
        long bytesCopied = 0;

        foreach (var (sessionId, size) in unbacked)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = $"{sessionId}.kv";
            var srcPath = Path.Combine(_config.StoreDirectory.FullName, fileName);
            var dstPath = Path.Combine(backupRootDir.FullName, fileName);

            try
            {
                if (!File.Exists(srcPath))
                    continue;

                File.Copy(srcPath, dstPath, overwrite: false);
                await _metadata.KvMarkBackedUpAsync(sessionId, dstPath, size, ct);
                copied++;
                bytesCopied += size;
            }
            catch (IOException) when (File.Exists(dstPath))
            {
                // A previous cycle copied the .kv but crashed before marking —
                // the destination already holds the bytes. Record it and move on.
                try { await _metadata.KvMarkBackedUpAsync(sessionId, dstPath, size, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Warning(ex, "Write-behind: mark .kv backed-up failed {SessionId} — continuing flush cycle", sessionId); }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Write-behind: skipped .kv {File} ({Reason}) — continuing flush cycle", fileName, ex.GetType().Name);
            }
        }

        sw.Stop();
        if (copied > 0)
        {
            _log.Information(
                "Write-behind: copied {Count} .kv manifests ({Bytes:F2} MB) in {Elapsed}ms",
                copied, bytesCopied / 1_048_576.0, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// FREE-AFTER-BACKUP (#470): evicts backed-up files from the tmpfs RAM front
    /// once the durable SSD copy is in place AND no session referencing them has
    /// been updated within <c>RamKeepRecentHours</c> (same freshness semantics as
    /// the retention GC, so warm/in-flight sessions are never victims). RAM is an
    /// L1 front keeping only recent files; backed-up chunks and .kv blobs that
    /// qualify are deleted from tmpfs so the 40 GB mount stays below ENOSPC.
    /// <c>0</c> hours disables free-after-backup entirely (backward compatible).
    /// </summary>
    /// <returns>Number of RAM files evicted.</returns>
    internal async Task<int> FreeBackedUpFromRamAsync(CancellationToken ct)
    {
        if (_config.RamKeepRecentHours <= 0)
            return 0;

        var keepRecent = TimeSpan.FromHours(_config.RamKeepRecentHours);
        var freed = 0;

        var chunkHashes = await _metadata.GetFreeableBackedUpChunkHashesAsync(keepRecent, BatchSize, ct);
        foreach (var hash in chunkHashes)
        {
            ct.ThrowIfCancellationRequested();
            if (_chunkStore.EvictChunk(hash))
                freed++;
        }

        var kvSessionIds = await _metadata.GetFreeableBackedUpKvAsync(keepRecent, BatchSize, ct);
        foreach (var sessionId in kvSessionIds)
        {
            ct.ThrowIfCancellationRequested();
            var kvPath = Path.Combine(_config.StoreDirectory.FullName, $"{sessionId}.kv");
            try
            {
                if (File.Exists(kvPath))
                {
                    File.Delete(kvPath);
                    freed++;
                    _log.Information("Write-behind: evicted .kv {SessionId} from RAM (backed up)", sessionId);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Write-behind: failed to evict .kv {SessionId} from RAM", sessionId);
            }
        }

        if (freed > 0)
        {
            _log.Information(
                "Write-behind: free-after-backup evicted {Count} backed-up files from RAM (keep_recent_hours={Hours})",
                freed, _config.RamKeepRecentHours);
        }
        return freed;
    }
}
