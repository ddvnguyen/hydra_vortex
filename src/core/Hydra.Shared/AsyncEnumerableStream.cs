using System.Runtime.CompilerServices;

namespace Hydra.Shared;

public sealed class AsyncEnumerableStream : Stream
{
    private readonly IAsyncEnumerable<byte[]> _source;
    private readonly CancellationToken _ct;
    private IAsyncEnumerator<byte[]>? _enumerator;
    private byte[]? _current;
    private int _position;

    public AsyncEnumerableStream(IAsyncEnumerable<byte[]> source, CancellationToken ct)
    {
        _source = source;
        _ct = ct;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use ReadAsync");
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _enumerator ??= _source.GetAsyncEnumerator(_ct);

        while ((_current is null || _position >= _current.Length) && await _enumerator.MoveNextAsync())
        {
            _current = _enumerator.Current;
            _position = 0;
        }

        if (_current is null || _position >= _current.Length)
            return 0;

        var toCopy = Math.Min(count, _current.Length - _position);
        Array.Copy(_current, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _enumerator is not null)
            _ = _enumerator.DisposeAsync().AsTask();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync();
        base.Dispose(disposing: false);
    }
}
