using System.IO.Pipelines;
using Hydra.Store;

namespace Tests.Store;

public sealed class StorageEngineTests : IDisposable
{
    private readonly DirectoryInfo _storeDir;
    private readonly StorageEngine _engine;

    public StorageEngineTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-test-{Guid.NewGuid():N}"));
        _engine = new StorageEngine(_storeDir);
    }

    public void Dispose()
    {
        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    [Fact]
    public async Task PutAndGet_SmallData_RoundTrip()
    {
        var key = "test/small.txt";
        var data = "Hello, Hydra Store!"u8.ToArray();

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.CompleteAsync();

        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);

        var file = await _engine.GetAsync(key, CancellationToken.None);
        Assert.NotNull(file);
        Assert.True(file!.Exists);

        var readData = await File.ReadAllBytesAsync(file.FullName);
        Assert.Equal(data, readData);
    }

    [Fact]
    public async Task PutAndGet_LargeData_RoundTrip()
    {
        var key = "test/large.bin";
        var data = new byte[10_000_000]; // 10 MB
        new Random(42).NextBytes(data);

        var pipe = new Pipe();

        // Write and read concurrently to avoid Pipe backpressure
        var writeTask = Task.Run(async () =>
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var chunk = Math.Min(65536, data.Length - offset);
                await pipe.Writer.WriteAsync(data.AsMemory(offset, chunk));
                offset += chunk;
            }
            await pipe.Writer.CompleteAsync();
        });

        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);
        await writeTask;

        var file = await _engine.GetAsync(key, CancellationToken.None);
        Assert.NotNull(file);
        Assert.Equal(data.Length, file!.Length);

        var readData = await File.ReadAllBytesAsync(file.FullName);
        Assert.Equal(data, readData);
    }

    [Fact]
    public async Task PutAsync_StreamsPipeDirectly()
    {
        var key = "test/streamed.bin";
        var data = new byte[5_000_000];
        new Random(99).NextBytes(data);

        var pipe = new Pipe();

        // Write in chunks to simulate streaming
        var writeTask = Task.Run(async () =>
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var chunk = Math.Min(65536, data.Length - offset);
                await pipe.Writer.WriteAsync(data.AsMemory(offset, chunk));
                offset += chunk;
            }
            await pipe.Writer.CompleteAsync();
        });

        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);
        await writeTask;

        var file = await _engine.GetAsync(key, CancellationToken.None);
        Assert.NotNull(file);
        Assert.Equal(data.Length, file!.Length);

        var readData = await File.ReadAllBytesAsync(file.FullName);
        Assert.Equal(data, readData);
    }

    [Fact]
    public async Task Get_NonExistentKey_ReturnsNull()
    {
        var file = await _engine.GetAsync("nonexistent", CancellationToken.None);
        Assert.Null(file);
    }

    [Fact]
    public async Task Delete_ExistingKey_ReturnsTrue()
    {
        var key = "test/deleteme.txt";
        var data = "delete me"u8.ToArray();

        // Put
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.CompleteAsync();
        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);

        // Verify exists
        Assert.NotNull(await _engine.GetAsync(key, CancellationToken.None));

        // Delete
        var deleted = await _engine.DeleteAsync(key, CancellationToken.None);
        Assert.True(deleted);

        // Verify gone
        Assert.Null(await _engine.GetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_NonExistentKey_ReturnsFalse()
    {
        var deleted = await _engine.DeleteAsync("ghost", CancellationToken.None);
        Assert.False(deleted);
    }

    [Fact]
    public async Task Stat_ReturnsCorrectSize()
    {
        var key = "test/stat_me.bin";
        var data = new byte[123_456];
        new Random(1).NextBytes(data);

        var pipe = new Pipe();
        var writeTask = Task.Run(async () =>
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var chunk = Math.Min(65536, data.Length - offset);
                await pipe.Writer.WriteAsync(data.AsMemory(offset, chunk));
                offset += chunk;
            }
            await pipe.Writer.CompleteAsync();
        });

        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);
        await writeTask;

        var stat = await _engine.StatAsync(key, CancellationToken.None);
        Assert.NotNull(stat);
        Assert.Equal(data.Length, stat!.Size);
    }

    [Fact]
    public async Task Stat_NonExistentKey_ReturnsNull()
    {
        var stat = await _engine.StatAsync("missing", CancellationToken.None);
        Assert.Null(stat);
    }

    [Fact]
    public async Task List_WithPrefix_ReturnsMatching()
    {
        var keys = new[]
        {
            "alpha/one.txt",
            "alpha/two.txt",
            "beta/one.txt",
            "gamma/deep/three.txt",
        };

        foreach (var k in keys)
        {
            var pipe = new Pipe();
            var bytes = System.Text.Encoding.UTF8.GetBytes(k[^5..]);
            await pipe.Writer.WriteAsync(bytes);
            await pipe.Writer.CompleteAsync();
            await _engine.PutAsync(k, pipe.Reader, 5, CancellationToken.None);
        }

        var alphaFiles = new List<string>();
        await foreach (var f in _engine.ListAsync("alpha/", CancellationToken.None))
            alphaFiles.Add(f);

        Assert.Equal(2, alphaFiles.Count);
        Assert.Contains("alpha/one.txt", alphaFiles);
        Assert.Contains("alpha/two.txt", alphaFiles);

        var allFiles = new List<string>();
        await foreach (var f in _engine.ListAsync("", CancellationToken.None))
            allFiles.Add(f);

        Assert.Equal(4, allFiles.Count);
    }

    [Fact]
    public async Task List_NoMatch_ReturnsEmpty()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync("data"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        await _engine.PutAsync("some/key.txt", pipe.Reader, 4, CancellationToken.None);

        var results = new List<string>();
        await foreach (var f in _engine.ListAsync("zzz/", CancellationToken.None))
            results.Add(f);

        Assert.Empty(results);
    }

    [Fact]
    public void PathTraversal_DoubleDot_Rejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            // Trigger path sanitization via PutAsync
            var pipe = new Pipe();
            pipe.Writer.Complete();
            _engine.PutAsync("../../etc/passwd", pipe.Reader, 0, CancellationToken.None).GetAwaiter().GetResult();
        });

        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void PathTraversal_AbsolutePath_Rejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var pipe = new Pipe();
            pipe.Writer.Complete();
            _engine.PutAsync("/etc/passwd", pipe.Reader, 0, CancellationToken.None).GetAwaiter().GetResult();
        });

        Assert.Contains("'/'", ex.Message);
    }

    [Fact]
    public async Task GetDebugStats_ReturnsCounts()
    {
        for (int i = 0; i < 5; i++)
        {
            var data = new byte[100];
            var pipe = new Pipe();
            await pipe.Writer.WriteAsync(data);
            await pipe.Writer.CompleteAsync();
            await _engine.PutAsync($"debug/file{i}.bin", pipe.Reader, data.Length, CancellationToken.None);
        }

        var stats = await _engine.GetDebugStatsAsync(CancellationToken.None);
        Assert.Equal(5, stats.FileCount);
        Assert.Equal(500, stats.TotalBytes);
    }

    [Fact]
    public async Task PutAsync_WriteFailure_DoesNotLeavePartialFile()
    {
        var key = "partial/aborted.bin";
        var data = new byte[5_000_000];
        new Random(42).NextBytes(data);

        var pipe = new Pipe();

        _ = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(data);
            await pipe.Writer.CompleteAsync();
        });

        var reader = new FailingPipeReader(pipe.Reader, failAfterBytes: 1_000_000);

        await Assert.ThrowsAnyAsync<Exception>(
            () => _engine.PutAsync(key, reader, data.Length, CancellationToken.None));

        var file = await _engine.GetAsync(key, CancellationToken.None);
        Assert.Null(file);
    }

    [Fact]
    public async Task PutAsync_AtomicRename_NoTempFileLeftBehind()
    {
        var key = "atomic/normal.bin";
        var data = "atomic write"u8.ToArray();

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.CompleteAsync();

        await _engine.PutAsync(key, pipe.Reader, data.Length, CancellationToken.None);

        var file = await _engine.GetAsync(key, CancellationToken.None);
        Assert.NotNull(file);
        Assert.True(file!.Exists);

        var tmpPath = file.FullName + ".tmp";
        Assert.False(File.Exists(tmpPath), "temp file should be cleaned up after successful write");
    }

    private sealed class FailingPipeReader : PipeReader
    {
        private readonly PipeReader _inner;
        private readonly long _failAfterBytes;
        private long _bytesRead;

        public FailingPipeReader(PipeReader inner, long failAfterBytes)
        {
            _inner = inner;
            _failAfterBytes = failAfterBytes;
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken ct = default)
        {
            var result = await _inner.ReadAsync(ct);
            _bytesRead += result.Buffer.Length;
            if (_bytesRead >= _failAfterBytes)
                throw new IOException("Simulated disk full");
            return result;
        }

        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => _inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        public override bool TryRead(out ReadResult result) => _inner.TryRead(out result);
    }
}
