using System.Buffers;
using System.Threading.Channels;

namespace Sockless.Port.Docker.Tunnel;

/// <summary>
/// One stream over a <see cref="TunnelMux"/>, used like a socket's stream. Reads end (return 0)
/// when the other side half-closes or resets; they fail when the tunnel itself goes down.
/// Disposing resets the stream unless both sides have already half-closed: what was written
/// before still arrives, but the other side's further writes fail, as with a closed socket.
/// </summary>
public sealed class TunnelStream : Stream
{
    private readonly TunnelMux _mux;
    private readonly Channel<(byte[] Buffer, int Length)> _incoming =
        Channel.CreateUnbounded<(byte[], int)>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _windowChanged = new(0);

    private (byte[] Buffer, int Length)? _current;
    private int _offset;
    private int _buffered;
    private int _consumed;
    private int _sendWindow = TunnelMux.Window;
    private volatile bool _finSent;
    private volatile bool _finReceived;
    private volatile bool _reset;
    private volatile bool _disposed;
    private volatile IOException? _failure;

    internal TunnelStream(TunnelMux mux, uint id)
    {
        _mux = mux;
        Id = id;
    }

    public uint Id { get; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    // ---------------------------------------------------------------- reading

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
            return 0;

        while (_current is null)
        {
            if (_incoming.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
                break;
            }
            if (_failure is { } failure)
                throw new IOException(failure.Message, failure);
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_failure is { } closed)
                    throw new IOException(closed.Message, closed);
                return 0;
            }
        }

        var (array, length) = _current.Value;
        var count = Math.Min(buffer.Length, length - _offset);
        array.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        if (_offset == length)
        {
            ArrayPool<byte>.Shared.Return(array);
            _current = null;
        }

        Interlocked.Add(ref _buffered, -count);
        _consumed += count;
        // Grant the other side more once a quarter of the window has been read.
        if (_consumed >= TunnelMux.Window / 4 && !_finReceived && !_reset)
        {
            var grant = _consumed;
            _consumed = 0;
            await _mux.TrySendAsync(TunnelMux.WindowFrame, Id, grant);
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // ---------------------------------------------------------------- writing

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            while (!buffer.IsEmpty)
            {
                ThrowIfCantWrite();
                var window = Volatile.Read(ref _sendWindow);
                if (window <= 0)
                {
                    await _windowChanged.WaitAsync(cancellationToken);
                    continue;
                }
                var count = Math.Min(Math.Min(window, TunnelMux.MaxPayload), buffer.Length);
                Interlocked.Add(ref _sendWindow, -count);
                await _mux.SendAsync(TunnelMux.DataFrame, Id, buffer[..count], cancellationToken);
                buffer = buffer[count..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Tells the other side nothing more will be written (its reads end), while this side can still read.</summary>
    public async ValueTask ShutdownWriteAsync()
    {
        if (_finSent || _reset || _disposed)
            return;
        await _writeLock.WaitAsync();
        try
        {
            if (_finSent)
                return;
            _finSent = true;
            await _mux.TrySendAsync(TunnelMux.FinFrame, Id);
        }
        finally
        {
            _writeLock.Release();
        }
        if (_finReceived)
            _mux.Forget(Id);
    }

    private void ThrowIfCantWrite()
    {
        if (_failure is { } failure)
            throw new IOException(failure.Message, failure);
        if (_reset)
            throw new IOException("The other side closed the stream.");
        if (_finSent)
            throw new InvalidOperationException("This side already finished writing.");
    }

    // ---------------------------------------------------------------- from the mux

    /// <summary>Data from the other side. False when it exceeds the window.</summary>
    internal bool Received(ReadOnlySpan<byte> data)
    {
        if (_disposed || _finReceived || _reset || data.IsEmpty)
            return true;
        if (Interlocked.Add(ref _buffered, data.Length) > TunnelMux.Window)
            return false;
        var copy = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(copy);
        if (!_incoming.Writer.TryWrite((copy, data.Length)))
            ArrayPool<byte>.Shared.Return(copy);
        return true;
    }

    internal void RemoteFinished()
    {
        _finReceived = true;
        _incoming.Writer.TryComplete();
        if (_finSent)
            _mux.Forget(Id);
    }

    internal void RemoteReset()
    {
        _reset = true;
        _incoming.Writer.TryComplete();
        _windowChanged.Release();
    }

    internal void Granted(int increment)
    {
        if (increment <= 0)
            return;
        Interlocked.Add(ref _sendWindow, increment);
        _windowChanged.Release();
    }

    /// <summary>The tunnel (or this stream) broke: reads and writes fail with <paramref name="failure"/>.</summary>
    internal void Abort(IOException failure)
    {
        _failure ??= failure;
        _reset = true;
        _incoming.Writer.TryComplete();
        _windowChanged.Release();
        _mux.Forget(Id);
    }

    // ---------------------------------------------------------------- closing

    protected override void Dispose(bool disposing)
    {
        if (disposing && MarkClosed())
            _ = _mux.TrySendAsync(TunnelMux.ResetFrame, Id).AsTask();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (MarkClosed())
            await _mux.TrySendAsync(TunnelMux.ResetFrame, Id);
        await base.DisposeAsync();
    }

    /// <summary>Marks the stream disposed. True when the other side must be told with a reset.</summary>
    private bool MarkClosed()
    {
        if (_disposed)
            return false;
        _disposed = true;
        var tell = !_reset && !(_finSent && _finReceived);
        _reset = true;
        _incoming.Writer.TryComplete();
        // A read may still hold the current buffer (dispose can race it): leave that one to the GC.
        while (_incoming.Reader.TryRead(out var pending))
            ArrayPool<byte>.Shared.Return(pending.Buffer);
        _windowChanged.Release();
        _mux.Forget(Id);
        return tell;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
