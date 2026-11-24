using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Sockless.Port.Docker.Tunnel;

/// <summary>
/// Many byte streams over one WebSocket. Every frame is one binary message: a type byte, the
/// stream id (big-endian) and the payload. One side opens streams (odd ids), the other accepts
/// them; either side may write, half-close (<see cref="TunnelStream.ShutdownWrite"/>) or reset.
/// Each stream has its own flow-control window, so a stream nobody reads (a paused log tail)
/// stalls only itself and never the tunnel: the reader loop never waits on a stream.
/// </summary>
public sealed class TunnelMux : IAsyncDisposable
{
    internal const byte OpenFrame = 1;
    internal const byte DataFrame = 2;
    internal const byte FinFrame = 3;
    internal const byte ResetFrame = 4;
    internal const byte WindowFrame = 5;
    internal const int HeaderSize = 5;

    /// <summary>Largest payload in one data frame.</summary>
    public const int MaxPayload = 16 * 1024;

    /// <summary>Bytes a side may send on a stream before the reader grants more.</summary>
    public const int Window = 256 * 1024;

    /// <summary>Streams open at once; more are refused (reset).</summary>
    public const int MaxStreams = 512;

    private readonly WebSocket _socket;
    private readonly bool _opens;
    private readonly ConcurrentDictionary<uint, TunnelStream> _streams = new();
    private readonly Channel<TunnelStream> _accepted = Channel.CreateBounded<TunnelStream>(new BoundedChannelOptions(64) { SingleReader = false });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _closeGate = new();
    private readonly Task _reader;
    private uint _nextId;
    private volatile string? _closeReason;

    /// <param name="opensStreams">True on the side that calls <see cref="OpenAsync"/> (the server), false on the side that accepts (the agent).</param>
    public TunnelMux(WebSocket socket, bool opensStreams)
    {
        _socket = socket;
        _opens = opensStreams;
        _nextId = opensStreams ? 1u : 2u;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Completes (never faults) when the tunnel has closed, for whatever reason.</summary>
    public Task Completion => _reader;

    public bool IsClosed => _closed.IsCancellationRequested;

    /// <summary>Why the tunnel closed, once it has.</summary>
    public string? CloseReason => _closeReason;

    internal int StreamCount => _streams.Count;

    /// <summary>Opens a stream to the other side. It's usable at once: data written now arrives after the open.</summary>
    /// <exception cref="IOException">The tunnel is closed, or too many streams are open.</exception>
    public async ValueTask<TunnelStream> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (!_opens)
            throw new InvalidOperationException("This side of the tunnel accepts streams; the other side opens them.");
        ThrowIfClosed();
        if (_streams.Count >= MaxStreams)
            throw new IOException($"Too many streams are open over the tunnel ({MaxStreams}).");

        var id = Interlocked.Add(ref _nextId, 2) - 2;
        var stream = new TunnelStream(this, id);
        _streams[id] = stream;
        try
        {
            await SendAsync(OpenFrame, id, default, cancellationToken);
        }
        catch
        {
            _streams.TryRemove(id, out _);
            throw;
        }
        return stream;
    }

    /// <summary>The next stream the other side opened, or null once the tunnel has closed.</summary>
    public async ValueTask<TunnelStream?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- receiving

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[HeaderSize + MaxPayload];
        string reason;
        try
        {
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                        throw new InvalidDataException("The other side sent a frame larger than the tunnel allows.");
                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), _closed.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = _socket.CloseStatusDescription is { Length: > 0 } description ? $"The other side closed the tunnel: {description}" : "The other side closed the tunnel.";
                    // Answer the close handshake so the other side's close completes.
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _sendLock.WaitAsync(timeout.Token);
                        try
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary || count < HeaderSize)
                    throw new InvalidDataException("The other side sent something that isn't a tunnel frame.");

                await HandleAsync(buffer[0], BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1)), buffer.AsMemory(HeaderSize, count - HeaderSize));
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            reason = _closeReason ?? "The tunnel was closed.";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or ObjectDisposedException)
        {
            reason = ex is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }
                ? "The tunnel's connection dropped."
                : $"The tunnel failed: {ex.Message}";
        }
        Close(reason);
    }

    private async ValueTask HandleAsync(byte type, uint id, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case OpenFrame:
                if (_opens || id % 2 != 1 || _streams.ContainsKey(id))
                    throw new InvalidDataException("The other side opened a stream it may not open.");
                if (_streams.Count >= MaxStreams)
                {
                    await TrySendAsync(ResetFrame, id);
                    return;
                }
                var stream = new TunnelStream(this, id);
                _streams[id] = stream;
                if (!_accepted.Writer.TryWrite(stream))
                {
                    _streams.TryRemove(id, out _);
                    await TrySendAsync(ResetFrame, id);
                }
                return;

            case DataFrame:
                if (_streams.TryGetValue(id, out var target) && !target.Received(payload.Span))
                {
                    // More than the window: the other side broke the protocol on this stream.
                    target.Abort(new IOException("The other side sent more than the stream's window."));
                    await TrySendAsync(ResetFrame, id);
                }
                return;

            case FinFrame:
                if (_streams.TryGetValue(id, out var finished))
                    finished.RemoteFinished();
                return;

            case ResetFrame:
                if (_streams.TryRemove(id, out var reset))
                    reset.RemoteReset();
                return;

            case WindowFrame:
                if (payload.Length != 4)
                    throw new InvalidDataException("The other side sent a malformed window update.");
                if (_streams.TryGetValue(id, out var granted))
                    granted.Granted(BinaryPrimitives.ReadInt32BigEndian(payload.Span));
                return;

            default:
                throw new InvalidDataException($"The other side sent an unknown frame type {type}.");
        }
    }

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// Sends one frame. <paramref name="cancellationToken"/> only bounds the wait for the socket:
    /// cancelling a WebSocket send midway would break the whole tunnel.
    /// </summary>
    internal async ValueTask SendAsync(byte type, uint id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var length = HeaderSize + payload.Length;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            frame[0] = type;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), id);
            payload.Span.CopyTo(frame.AsSpan(HeaderSize));

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfClosed();
                await _socket.SendAsync(frame.AsMemory(0, length), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            Close($"The tunnel failed: {ex.Message}");
            throw new IOException(_closeReason, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Sends a frame when the tunnel is still up, ignoring failures (resets and closes are best effort).</summary>
    internal async ValueTask TrySendAsync(byte type, uint id, int? window = null)
    {
        if (IsClosed)
            return;
        try
        {
            if (window is { } increment)
            {
                var payload = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(payload, increment);
                await SendAsync(type, id, payload, CancellationToken.None);
            }
            else
            {
                await SendAsync(type, id, default, CancellationToken.None);
            }
        }
        catch (IOException)
        {
        }
    }

    internal void Forget(uint id) => _streams.TryRemove(id, out _);

    private void ThrowIfClosed()
    {
        if (IsClosed)
            throw new IOException(_closeReason ?? "The tunnel is closed.");
    }

    // ---------------------------------------------------------------- closing

    /// <summary>Ends the tunnel: every stream fails with <paramref name="reason"/>, the socket is aborted.</summary>
    public void Close(string reason)
    {
        lock (_closeGate)
        {
            if (_closed.IsCancellationRequested)
                return;
            _closeReason = reason;
            _closed.Cancel();
        }
        _accepted.Writer.TryComplete();
        while (_accepted.Reader.TryRead(out var unaccepted))
            unaccepted.Dispose();
        foreach (var stream in _streams.Values)
            stream.Abort(new IOException(reason));
        _streams.Clear();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Closes the WebSocket politely (briefly waiting for the other side), then ends the tunnel.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsClosed && _socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _sendLock.WaitAsync(timeout.Token);
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                finally
                {
                    _sendLock.Release();
                }
                await _reader.WaitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or TimeoutException)
            {
            }
        }
        Close("The tunnel was closed.");
        await _reader;
        _socket.Dispose();
    }
}
