using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hydra.Store;

public sealed class StorageEngine
{
    private readonly DirectoryInfo _storeDir;

    public StorageEngine(DirectoryInfo storeDir)
    {
        _storeDir = storeDir;

        if (!_storeDir.Exists)
            _storeDir.Create();
    }

    public DirectoryInfo StoreDirectory => _storeDir;

    public async Task PutAsync(string key, PipeReader source, long size, CancellationToken ct)
    {
        var path = GetSafePath(key);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        try
        {
            await using var fileStream = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous);

            long consumed = 0;
            while (consumed < size)
            {
                var result = await source.ReadAsync(ct);
                if (result.IsCanceled)
                    throw new OperationCanceledException();
                if (result.IsCompleted && result.Buffer.IsEmpty)
                    throw new EndOfStreamException(
                        $"Unexpected end of stream reading '{key}' ({size - consumed} bytes remaining)");

                var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, size - consumed));
                foreach (var segment in slice)
                {
                    await fileStream.WriteAsync(segment, ct);
                    consumed += segment.Length;
                }

                source.AdvanceTo(slice.End);
            }

            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    public Task<FileInfo?> GetAsync(string key, CancellationToken ct)
    {
        var path = GetSafePath(key);
        if (!File.Exists(path))
            return Task.FromResult<FileInfo?>(null);

        return Task.FromResult<FileInfo?>(new FileInfo(path));
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        var path = GetSafePath(key);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<StatResult?> StatAsync(string key, CancellationToken ct)
    {
        var path = GetSafePath(key);
        if (!File.Exists(path))
            return Task.FromResult<StatResult?>(null);

        var fi = new FileInfo(path);
        return Task.FromResult<StatResult?>(new StatResult(fi.Name, fi.Length, fi.LastWriteTimeUtc));
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = _storeDir.FullName;

        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relPath = Path.GetRelativePath(dir, file);
            if (relPath.StartsWith(prefix, StringComparison.Ordinal))
                yield return relPath;
        }
    }

    public Task<DebugStats> GetDebugStatsAsync(CancellationToken ct)
    {
        if (!_storeDir.Exists)
        {
            StoreMetrics.FileCount.Set(0);
            return Task.FromResult(new DebugStats(0, 0, _storeDir.FullName));
        }

        var files = _storeDir.EnumerateFiles("*", SearchOption.AllDirectories);
        var count = 0;
        long totalBytes = 0;

        foreach (var f in files)
        {
            if (f.Name.EndsWith(".tmp")) continue;
            count++;
            totalBytes += f.Length;
        }

        StoreMetrics.FileCount.Set(count);
        return Task.FromResult(new DebugStats(count, totalBytes, _storeDir.FullName));
    }

    private string GetSafePath(string key)
    {
        if (key.Contains(".."))
            throw new InvalidDataException("Key must not contain '..'");
        if (key.StartsWith('/') || key.StartsWith('\\'))
            throw new InvalidDataException("Key must not start with '/'");

        var path = Path.Combine(_storeDir.FullName, key);
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(_storeDir.FullName, StringComparison.Ordinal))
            throw new InvalidDataException("Path traversal detected");

        return fullPath;
    }
}

public sealed record DebugStats(
    int FileCount,
    long TotalBytes,
    string StorePath
);
