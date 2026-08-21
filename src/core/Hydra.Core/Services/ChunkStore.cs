using System.Collections.Concurrent;

namespace Hydra.Core;

public sealed class ChunkStore
{
    private readonly DirectoryInfo _chunksDir;
    private readonly ConcurrentDictionary<string, byte> _knownHashes = new();
    private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<ChunkStore>();

    public ChunkStore(DirectoryInfo storeDir)
    {
        _chunksDir = new DirectoryInfo(Path.Combine(storeDir.FullName, "chunks"));
        if (!_chunksDir.Exists)
            _chunksDir.Create();
        RebuildIndex();
    }

    /// <summary>
    /// Optional callback invoked when a chunk write hits ENOSPC on the tmpfs
    /// RAM front. Backed-up files have a durable SSD copy, so the write-behind
    /// service frees them (respecting the recent-session window) before the
    /// write is retried once (#470). Wired from Program.cs; null disables.
    /// </summary>
    public Func<CancellationToken, Task<int>>? FreeBackedUpOnEnospc { get; set; }

    public void RefreshIndex() => RebuildIndex();

    private void RebuildIndex()
    {
        _knownHashes.Clear();
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp")) continue;
            _knownHashes[file.Name] = 0;
        }
    }

    public int KnownChunkCount => _knownHashes.Count;
    public DirectoryInfo ChunksDirectory => _chunksDir;

    public async Task<bool> StoreChunkAsync(string hash, byte[] data, CancellationToken ct = default)
    {
        if (!_knownHashes.TryAdd(hash, 0))
            return false;

        var path = Path.Combine(_chunksDir.FullName, hash);
        var tmpPath = path + ".tmp";
        try
        {
            await WriteChunkFileAsync(tmpPath, path, data, ct);
            return true;
        }
        catch (IOException ex) when (IsNoSpaceLeftOnDevice(ex) && FreeBackedUpOnEnospc is not null)
        {
            // #470: ENOSPC on the tmpfs RAM front. Backed-up files already have
            // a durable SSD copy, so the free-after-backup callback evicts them
            // from RAM (respecting the recent-session window) to make room for
            // this new file — the "allow overwrite of new files" half. Retry the
            // write once; if it still fails, fall through to the shared cleanup.
            try
            {
                var freed = await FreeBackedUpOnEnospc(ct);
                _log.Information("chunk_store_enospc_freed_backed_up hash={Hash} freed={Freed}", hash, freed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception freeEx)
            {
                _log.Warning(freeEx, "chunk_store_enospc_free_failed hash={Hash}", hash);
            }

            try
            {
                await WriteChunkFileAsync(tmpPath, path, data, ct);
                return true;
            }
            catch
            {
                _knownHashes.TryRemove(hash, out _);
                try { File.Delete(tmpPath); } catch { }
                throw;
            }
        }
        catch
        {
            _knownHashes.TryRemove(hash, out _);
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private static async Task WriteChunkFileAsync(string tmpPath, string path, byte[] data, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(tmpPath, data, ct);
        File.Move(tmpPath, path, overwrite: true);
    }

    private static bool IsNoSpaceLeftOnDevice(IOException ex)
    {
        // Linux tmpfs reports ENOSPC as "No space left on device". Also accept
        // the HResult for ENOSPC (Unix errno 28 → 0x8007001C, Win32
        // ERROR_DISK_FULL → 0x80070070) and common surface text so a mislabeled
        // message can't silently skip the free-and-retry path.
        return ex.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
            || ex.HResult == unchecked((int)0x8007001C)
            || ex.HResult == unchecked((int)0x80070070);
    }

    public bool HasChunk(string hash)
    {
        return _knownHashes.ContainsKey(hash);
    }

    /// <summary>
    /// Free-after-backup (#470): evict a chunk's RAM copy after its durable SSD
    /// backup is in place and it is no longer referenced by a recent session.
    /// Deletes the tmpfs file and drops the in-memory index entry so subsequent
    /// <see cref="HasChunk"/>/<see cref="GetChunkPath"/> see it as absent (the
    /// backup dir still holds the durable copy). No-op when the file is gone.
    /// </summary>
    /// <returns>True when a RAM file was actually deleted.</returns>
    public bool EvictChunk(string hash)
    {
        var path = Path.Combine(_chunksDir.FullName, hash);
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "chunk_store_evict_failed hash={Hash}", hash);
            return false;
        }

        _knownHashes.TryRemove(hash, out _);
        _log.Information("chunk_store_evicted_from_ram hash={Hash}", hash);
        return true;
    }

    public string? GetChunkPath(string hash)
    {
        var path = Path.Combine(_chunksDir.FullName, hash);
        return File.Exists(path) ? path : null;
    }

    public async Task<ChunkStoreStats> GetStatsAsync(CancellationToken ct)
    {
        var totalChunks = _knownHashes.Count;
        long totalBytes = 0;
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp")) continue;
            totalBytes += file.Length;
        }
        return new ChunkStoreStats(totalChunks, 0, totalBytes);
    }
}

public sealed record ChunkStoreStats(
    int TotalChunks,
    int ManifestCount,
    long TotalBytes
);
