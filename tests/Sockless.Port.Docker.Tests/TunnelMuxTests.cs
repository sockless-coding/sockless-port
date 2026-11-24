using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Sockless.Port.Docker.Tunnel;

namespace Sockless.Port.Docker.Tests;

/// <summary>The tunnel's multiplexer over a pair of in-memory WebSockets.</summary>
public sealed class TunnelMuxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Streams_carry_bytes_both_ways()
    {
        await using var pair = TunnelPair.Create();
        await using var opened = await pair.Server.OpenAsync(Ct);
        await opened.WriteAsync("ping"u8.ToArray(), Ct);

        await using var accepted = (await pair.Agent.AcceptAsync(Ct))!;
        Assert.Equal("ping", await ReadAsync(accepted, 4));

        await accepted.WriteAsync("pong"u8.ToArray(), Ct);
        Assert.Equal("pong", await ReadAsync(opened, 4));
    }

    [Fact]
    public async Task Much_more_than_a_window_arrives_intact()
    {
        await using var pair = TunnelPair.Create();
        await using var opened = await pair.Server.OpenAsync(Ct);
        var data = RandomNumberGenerator.GetBytes(TunnelMux.Window * 8 + 123);

        var writing = Task.Run(async () =>
        {
            await opened.WriteAsync(data, Ct);
            await opened.ShutdownWriteAsync();
        }, Ct);
        await using var accepted = (await pair.Agent.AcceptAsync(Ct))!;
        using var received = new MemoryStream();
        await accepted.CopyToAsync(received, Ct);
        await writing;

        Assert.Equal(data, received.ToArray());
    }

    [Fact]
    public async Task A_stream_nobody_reads_stalls_only_itself()
    {
        await using var pair = TunnelPair.Create();
        await using var stalled = await pair.Server.OpenAsync(Ct);
        await using var busy = await pair.Server.OpenAsync(Ct);

        // Fill the first stream's window and then some: its writer waits, the tunnel doesn't.
        var blocked = stalled.WriteAsync(new byte[TunnelMux.Window + 1000], Ct).AsTask();
        await using var stalledAccepted = (await pair.Agent.AcceptAsync(Ct))!;
        await using var busyAccepted = (await pair.Agent.AcceptAsync(Ct))!;

        await busy.WriteAsync("still moving"u8.ToArray(), Ct);
        Assert.Equal("still moving", await ReadAsync(busyAccepted, 12));
        Assert.False(blocked.IsCompleted);

        // Reading the first stream lets its writer finish.
        var drained = new byte[TunnelMux.Window + 1000];
        await stalledAccepted.ReadExactlyAsync(drained, Ct);
        await blocked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Half_close_ends_the_other_sides_reads_but_not_its_writes()
    {
        await using var pair = TunnelPair.Create();
        await using var opened = await pair.Server.OpenAsync(Ct);
        await opened.WriteAsync("request"u8.ToArray(), Ct);
        await opened.ShutdownWriteAsync();

        await using var accepted = (await pair.Agent.AcceptAsync(Ct))!;
        Assert.Equal("request", await ReadAsync(accepted, 7));
        Assert.Equal(0, await accepted.ReadAsync(new byte[1], Ct));

        await accepted.WriteAsync("answer"u8.ToArray(), Ct);
        Assert.Equal("answer", await ReadAsync(opened, 6));
    }

    [Fact]
    public async Task Disposing_delivers_what_was_written_then_refuses_more()
    {
        await using var pair = TunnelPair.Create();
        var opened = await pair.Server.OpenAsync(Ct);
        await opened.WriteAsync("last words"u8.ToArray(), Ct);
        await opened.DisposeAsync();

        await using var accepted = (await pair.Agent.AcceptAsync(Ct))!;
        Assert.Equal("last words", await ReadAsync(accepted, 10));
        Assert.Equal(0, await accepted.ReadAsync(new byte[1], Ct));
        await Assert.ThrowsAsync<IOException>(async () => await accepted.WriteAsync("too late"u8.ToArray(), Ct));
    }

    [Fact]
    public async Task Closing_the_tunnel_fails_open_streams_and_new_ones()
    {
        await using var pair = TunnelPair.Create();
        await using var opened = await pair.Server.OpenAsync(Ct);
        await using var accepted = (await pair.Agent.AcceptAsync(Ct))!;
        var reading = accepted.ReadAsync(new byte[1], Ct).AsTask();

        pair.Server.Close("Shutting down.");

        await Assert.ThrowsAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await pair.Agent.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(pair.Agent.IsClosed);
        Assert.Null(await pair.Agent.AcceptAsync(Ct));
        await Assert.ThrowsAsync<IOException>(async () => await pair.Server.OpenAsync(Ct));
    }

    [Fact]
    public async Task Disposing_closes_politely_and_the_other_side_says_why()
    {
        var pair = TunnelPair.Create();
        await pair.Agent.DisposeAsync();

        await pair.Server.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("The other side closed the tunnel.", pair.Server.CloseReason);
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Many_streams_run_at_once()
    {
        await using var pair = TunnelPair.Create();
        var echo = Task.Run(async () =>
        {
            while (await pair.Agent.AcceptAsync(Ct) is { } stream)
            {
                _ = Task.Run(async () =>
                {
                    await using (stream)
                    {
                        await stream.CopyToAsync(stream, Ct);
                        await stream.ShutdownWriteAsync();
                    }
                }, Ct);
            }
        }, Ct);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            await using var stream = await pair.Server.OpenAsync(Ct);
            var data = RandomNumberGenerator.GetBytes(20_000 + i);
            var write = Task.Run(async () =>
            {
                await stream.WriteAsync(data, Ct);
                await stream.ShutdownWriteAsync();
            }, Ct);
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, Ct);
            await write;
            Assert.Equal(data, received.ToArray());
        }));
        pair.Server.Close("Done.");
        await echo.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Theory]
    [InlineData("https://port.example.com", "wss://port.example.com/agent/tunnel?endpoint=7")]
    [InlineData("http://10.0.0.5:8080", "ws://10.0.0.5:8080/agent/tunnel?endpoint=7")]
    [InlineData("https://example.com/port/", "wss://example.com/port/agent/tunnel?endpoint=7")]
    public void The_tunnel_address_is_below_the_servers(string server, string expected) =>
        Assert.Equal(expected, TunnelProtocol.Address(new Uri(server), TunnelProtocol.EndpointParameter, "7").ToString());

    private static async Task<string> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, Ct);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>Two muxes joined by in-memory WebSockets: the server opens streams, the agent accepts.</summary>
    private sealed class TunnelPair : IAsyncDisposable
    {
        private TunnelPair(TunnelMux server, TunnelMux agent)
        {
            Server = server;
            Agent = agent;
        }

        public TunnelMux Server { get; }

        public TunnelMux Agent { get; }

        public static TunnelPair Create()
        {
            var toAgent = new Pipe();
            var toServer = new Pipe();
            var serverSocket = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader.AsStream(), toAgent.Writer.AsStream()),
                new WebSocketCreationOptions { IsServer = true });
            var agentSocket = WebSocket.CreateFromStream(new DuplexStream(toAgent.Reader.AsStream(), toServer.Writer.AsStream()),
                new WebSocketCreationOptions { IsServer = false });
            return new TunnelPair(new TunnelMux(serverSocket, opensStreams: true), new TunnelMux(agentSocket, opensStreams: false));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Agent.DisposeAsync();
        }
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override void Flush() => write.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
