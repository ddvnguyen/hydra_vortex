using Hydra.Store;

namespace Tests.Store;

public sealed class StoreConfigTests
{
    [Fact]
    public void Validate_DefaultConfig_DoesNotThrow()
    {
        var cfg = new StoreConfig();
        var ex = Record.Exception(() => cfg.Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_NonexistentStoreDir_DoesNotThrow()
    {
        var cfg = new StoreConfig { StoreDir = "/nonexistent/hydra-store" };
        var ex = Record.Exception(() => cfg.Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_InvalidPort_Throws()
    {
        var cfg = new StoreConfig { Port = 0, StoreDir = "/tmp" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_InvalidDebugPort_Throws()
    {
        var cfg = new StoreConfig { DebugHttpPort = -1, StoreDir = "/tmp" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_DuplicatePorts_Throws()
    {
        var cfg = new StoreConfig
        {
            Port = 9500,
            DebugHttpPort = 9500,
            StoreDir = "/tmp",
        };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_EmptyPgConn_Throws()
    {
        var cfg = new StoreConfig { PgConn = "", StoreDir = "/tmp" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        using var tmp = new TempDir();
        var cfg = new StoreConfig
        {
            Port = 9500,
            DebugHttpPort = 9501,
            StoreDir = tmp.Path,
            PgConn = "Host=localhost;Database=test",
        };
        var ex = Record.Exception(() => cfg.Validate());
        Assert.Null(ex);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hydra-cfg-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path);
        }
    }
}
