using Hydra.Core;

namespace Tests.Core;

public sealed class ChunkEngineTests
{
    [Fact]
    public void SmallData_SingleChunk()
    {
        var data = "hello"u8.ToArray();
        var chunks = ChunkEngine.ChunkAndHash(data);

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(5, chunks[0].Size);
        Assert.False(string.IsNullOrEmpty(chunks[0].Hash));
    }

    [Fact]
    public void LargeData_MultipleChunks()
    {
        var data = new byte[ChunkEngine.CHUNK_SIZE * 3 + 100];
        new Random(42).NextBytes(data);

        var chunks = ChunkEngine.ChunkAndHash(data);

        Assert.Equal(4, chunks.Count);
        Assert.Equal(ChunkEngine.CHUNK_SIZE, chunks[0].Size);
        Assert.Equal(ChunkEngine.CHUNK_SIZE, chunks[1].Size);
        Assert.Equal(ChunkEngine.CHUNK_SIZE, chunks[2].Size);
        Assert.Equal(100, chunks[3].Size);
    }

    [Fact]
    public void SameData_ProducesIdenticalHashes()
    {
        var data = new byte[ChunkEngine.CHUNK_SIZE * 2];
        new Random(42).NextBytes(data);

        var chunks1 = ChunkEngine.ChunkAndHash(data);
        var chunks2 = ChunkEngine.ChunkAndHash(data);

        Assert.Equal(chunks1.Count, chunks2.Count);
        for (int i = 0; i < chunks1.Count; i++)
        {
            Assert.Equal(chunks1[i].Hash, chunks2[i].Hash);
            Assert.Equal(chunks1[i].Size, chunks2[i].Size);
        }
    }

    [Fact]
    public void DifferentData_DifferentHashes()
    {
        var data1 = new byte[ChunkEngine.CHUNK_SIZE];
        var data2 = new byte[ChunkEngine.CHUNK_SIZE];
        new Random(1).NextBytes(data1);
        new Random(2).NextBytes(data2);

        var chunks1 = ChunkEngine.ChunkAndHash(data1);
        var chunks2 = ChunkEngine.ChunkAndHash(data2);

        Assert.NotEqual(chunks1[0].Hash, chunks2[0].Hash);
    }

    [Fact]
    public void EmptyData_NoChunks()
    {
        var chunks = ChunkEngine.ChunkAndHash([]);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ExactChunkSize_SingleChunk()
    {
        var data = new byte[ChunkEngine.CHUNK_SIZE];
        new Random(42).NextBytes(data);

        var chunks = ChunkEngine.ChunkAndHash(data);

        Assert.Single(chunks);
        Assert.Equal(ChunkEngine.CHUNK_SIZE, chunks[0].Size);
    }

    [Fact]
    public void CreateManifest_ContainsCorrectData()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "abc", 1024),
            new(1, "def", 1024),
        };

        var manifest = ChunkEngine.CreateManifest("sess_test", 100, 2048, chunks);

        Assert.Equal("sess_test", manifest.SessionId);
        Assert.Equal(1, manifest.Version);
        Assert.Equal(100, manifest.NPast);
        Assert.Equal(2048, manifest.TotalSize);
        Assert.Equal(2, manifest.Chunks.Count);
    }

    [Fact]
    public void DiffPlan_CorrectMissingHashes()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "hash_a", 1024),
            new(1, "hash_b", 1024),
            new(2, "hash_c", 1024),
        };

        var manifest = ChunkEngine.CreateManifest("sess_test", 0, 3072, chunks);
        var clientHashes = new List<string> { "hash_a", "hash_c" };

        var missing = ChunkEngine.DiffPlan(manifest, clientHashes);

        Assert.Single(missing);
        Assert.Contains("hash_b", missing);
    }

    [Fact]
    public void DiffPlan_AllHashesKnown_ReturnsEmpty()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "hash_a", 1024),
            new(1, "hash_b", 1024),
        };

        var manifest = ChunkEngine.CreateManifest("sess_test", 0, 2048, chunks);
        var clientHashes = new List<string> { "hash_a", "hash_b" };

        var missing = ChunkEngine.DiffPlan(manifest, clientHashes);

        Assert.Empty(missing);
    }

    [Fact]
    public void HashConsistency_SameInput_AlwaysSame()
    {
        var data = "The quick brown fox jumps over the lazy dog"u8.ToArray();
        var hash1 = ChunkEngine.ComputeHash(data);
        var hash2 = ChunkEngine.ComputeHash(data);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void LastChunk_SmallerThanChunkSize()
    {
        var data = new byte[ChunkEngine.CHUNK_SIZE + 500];
        new Random(42).NextBytes(data);

        var chunks = ChunkEngine.ChunkAndHash(data);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(ChunkEngine.CHUNK_SIZE, chunks[0].Size);
        Assert.Equal(500, chunks[1].Size);
    }
}
