namespace Sockless.Port.Moves;

/// <summary>
/// Reads through to another stream and counts the bytes, calling <paramref name="progress"/> with
/// the total after each read. Forward-only; disposing it disposes the inner stream.
/// </summary>
internal sealed class CountingStream(Stream inner, Action<long> progress) : Stream
{
    public long Count { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Counted(int read)
    {
        if (read > 0)
        {
            Count += read;
            progress(Count);
        }
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
