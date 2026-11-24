using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Agents;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Tunnel;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Agent.Tests;

/// <summary>
/// Edge mode on loopback: the agent dials the server's tunnel endpoint, is enrolled through the
/// tunnel, and the server reaches a (fake) engine through it over mutual TLS. Also renewal.
/// </summary>
public sealed class EdgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Edge_agent_dials_in_is_enrolled_and_relays_through_its_tunnel()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);

        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("edge-1", "", Edge: true, ServerUrl: host.Address.ToString()), Ct);
        Assert.Empty(errors);
        var pending = server.Connections.Get(id!.Value)!;
        Assert.True(pending.IsEdge);
        Assert.IsType<TunnelAddress>(pending.Address);
        Assert.Null(pending.Edge!.SeenAt);

        await using var agent = await AgentHarness.StartAsync(engine.Address, pending.Enrolment!.Token, server: host.Address);
        var connection = await server.WaitForAsync(id.Value, c => c.State is EndpointOnline, "online", Patience);

        Assert.Equal("Docker 29.0.0", connection.Engine!.DisplayName);
        Assert.Equal("edge", connection.Agent!.Info?.Mode);
        Assert.Equal(connection.Agent.Thumbprint, connection.Agent.Info?.CertificateSha256);
        Assert.Null(connection.Enrolment);
        Assert.NotNull(connection.Edge!.ConnectedSince);
        Assert.Equal(id, agent.Identity.EndpointId);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<PortDbContext>().Endpoints.SingleAsync(e => e.Id == id, Ct);
            Assert.True(row.Edge);
            Assert.Equal($"tunnel://sockless-port-agent-{id}", row.Address);
            Assert.Equal(host.Address.ToString().TrimEnd('/'), row.ServerUrl);
            Assert.NotNull(row.TunnelSeenAt);
            Assert.Null(row.EnrolmentTokenId);
        }

        // Ordinary requests and a hijacked exec both pass through the tunnel.
        var client = connection.Client!;
        Assert.Empty(await client.ListContainersAsync(cancellationToken: Ct));
        await using (var exec = await client.StartExecAsync("abc", Ct))
        {
            await exec.Stream.WriteAsync("hello"u8.ToArray(), Ct);
            await exec.Stream.FlushAsync(Ct);
            var buffer = new byte[5];
            await exec.Stream.ReadExactlyAsync(buffer, Ct);
            Assert.Equal("HELLO", Encoding.ASCII.GetString(buffer));
        }

        // The CLIs reach it through a bridge that does the TLS for them.
        var runDirectory = Path.Combine(server.DataPath, "tmp", "bridge-test");
        Directory.CreateDirectory(runDirectory);
        await using (var bridge = EngineBridge.Start(connection.Address, runDirectory))
        using (var viaBridge = await DockerClient.ConnectAsync(bridge.Address, cancellationToken: Ct))
        {
            Assert.Equal("29.0.0", viaBridge.ServerVersion.Version);
            Assert.Empty(await viaBridge.ListContainersAsync(cancellationToken: Ct));
        }
    }

    [Fact]
    public async Task Edge_agent_comes_back_after_a_restart_and_the_endpoint_shows_when_it_was_up()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);
        var (id, agent) = await EnrolEdgeAsync(server, host, engine);
        var dataPath = agent.DataPath;
        await agent.DisposeAsync();

        // The first failure is the stream breaking; the retries then say the tunnel is down.
        var down = await server.WaitForAsync(id, c => c.State is EndpointOffline offline && offline.Reason.Contains("tunnel is down", StringComparison.Ordinal),
            "offline with the tunnel down", Patience);
        Assert.Null(down.Edge!.ConnectedSince);
        Assert.NotNull(down.Edge.SeenAt);

        // Restarted without a token, as the update command does: it dials as its endpoint.
        await using var restarted = await AgentHarness.StartAsync(engine.Address, token: null, dataPath, server: host.Address);
        var connection = await server.WaitForAsync(id, c => c.State is EndpointOnline, "online again", Patience);
        Assert.NotNull(connection.Edge!.ConnectedSince);
        Assert.Empty(await connection.Client!.ListContainersAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_tunnel_that_cant_show_the_agents_certificate_is_not_taken()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);
        var (id, agent) = await EnrolEdgeAsync(server, host, engine);
        await using var real = agent;
        var tunnels = server.Services.GetRequiredService<AgentTunnels>();

        // An impostor dials as the endpoint and passes the server's streams to an agent that isn't enrolled (a self-signed certificate).
        await using var stranger = await AgentHarness.StartAsync(engine.Address, token: null);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(TunnelProtocol.SubProtocol);
        await socket.ConnectAsync(TunnelProtocol.Address(host.Address, TunnelProtocol.EndpointParameter, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct);
        await using var mux = new TunnelMux(socket, opensStreams: false);
        _ = Task.Run(async () =>
        {
            while (await mux.AcceptAsync(Ct) is { } stream)
                _ = SpliceAsync(stream, stranger.Port);
        }, Ct);

        // The server refuses it after the handshake, and keeps the real agent's tunnel.
        await mux.Completion.WaitAsync(Patience, Ct);
        Assert.True(tunnels.IsUp(id));
        var connection = server.Connections.Get(id)!;
        Assert.IsType<EndpointOnline>(connection.State);
        Assert.Empty(await connection.Client!.ListContainersAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Agents_the_server_doesnt_expect_are_turned_away()
    {
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);

        (string Parameter, string Value)[] strangers = [(TunnelProtocol.EndpointParameter, "999"), (TunnelProtocol.EnrolmentParameter, "AAAAAAAAAAAAAAAA")];
        foreach (var (parameter, value) in strangers)
        {
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(TunnelProtocol.SubProtocol);
            socket.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(TunnelProtocol.Address(host.Address, parameter, value), Ct));
            Assert.Equal(HttpStatusCode.NotFound, socket.HttpStatusCode);
        }

        // A direct endpoint never takes a tunnel, even with a valid enrolment.
        var (id, _) = await server.Registry.AddAgentAsync(new AgentEndpointInput("direct", "127.0.0.1:1"), Ct);
        var token = server.Connections.Get(id!.Value)!.Enrolment!.Token;
        using var direct = new ClientWebSocket();
        direct.Options.AddSubProtocol(TunnelProtocol.SubProtocol);
        direct.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => direct.ConnectAsync(TunnelProtocol.Address(host.Address, TunnelProtocol.EnrolmentParameter, token.Id), Ct));
        Assert.Equal(HttpStatusCode.NotFound, direct.HttpStatusCode);
    }

    [Fact]
    public async Task Enrolling_an_edge_agent_again_closes_its_tunnel()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);
        var (id, agent) = await EnrolEdgeAsync(server, host, engine);
        await using var _ = agent;

        Assert.Empty(await server.Registry.ReenrolAsync(id, "https://port.example.com/", Ct));

        var connection = await server.WaitForAsync(id, c => c.Edge!.ConnectedSince is null && c.Client is null, "its tunnel closed", Patience);
        Assert.Equal("https://port.example.com", connection.Edge!.ServerUrl);
        Assert.Null(connection.Agent!.Thumbprint);
        Assert.False(server.Services.GetRequiredService<AgentTunnels>().IsUp(id));
    }

    [Fact]
    public async Task Renewal_gives_an_edge_agent_a_new_certificate_without_losing_it()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var host = await TunnelHost.StartAsync(server);
        var (id, agent) = await EnrolEdgeAsync(server, host, engine);
        await using var _ = agent;

        await AssertRenewsAsync(server, agent, id);
    }

    [Fact]
    public async Task Renewal_works_for_direct_agents_too()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var (id, agent) = await EnrolmentTests.EnrolAsync(server, engine);
        await using var _ = agent;

        await AssertRenewsAsync(server, agent, id);
    }

    private static async Task AssertRenewsAsync(ServerHarness server, AgentHarness agent, int id)
    {
        var before = server.Connections.Get(id)!.Agent!.Thumbprint;
        var renewals = server.Services.GetRequiredService<AgentRenewals>();
        var started = DateTimeOffset.UtcNow;

        Assert.Null(await renewals.RenewAsync(id, Ct));

        // The renewal reconnects, so the live client knows only the new pin.
        var connection = await server.WaitForAsync(id, c => c.State is EndpointOnline online && online.Since > started, "online again", Patience);
        Assert.NotEqual(before, connection.Agent!.Thumbprint);
        Assert.Equal(connection.Agent!.Thumbprint, agent.Identity.CertificateThumbprint);
        Assert.Equal(connection.Agent.Thumbprint, connection.Agent.Info?.CertificateSha256);
        Assert.Empty(await connection.Client!.ListContainersAsync(cancellationToken: Ct));
        Assert.False(File.Exists(Path.Combine(agent.DataPath, AgentIdentity.RenewCertificateFile)));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<PortDbContext>().Endpoints.SingleAsync(e => e.Id == id, Ct);
            Assert.Equal(connection.Agent.Thumbprint, row.AgentThumbprint);
            Assert.Null(row.AgentPreviousThumbprint);
        }

        // The agent keeps the new identity when it restarts.
        var reloaded = new AgentIdentity(agent.DataPath);
        Assert.Equal(connection.Agent.Thumbprint, reloaded.CertificateThumbprint);
    }

    /// <summary>Adds an edge endpoint, starts its agent dialling the host, and waits until it's online.</summary>
    private static async Task<(int Id, AgentHarness Agent)> EnrolEdgeAsync(ServerHarness server, TunnelHost host, FakeEngine engine)
    {
        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("edge", "", Edge: true, ServerUrl: host.Address.ToString()), Ct);
        Assert.Empty(errors);
        var token = server.Connections.Get(id!.Value)!.Enrolment!.Token;
        var agent = await AgentHarness.StartAsync(engine.Address, token, server: host.Address);
        await server.WaitForAsync(id.Value, c => c.State is EndpointOnline, "online", Patience);
        return (id.Value, agent);
    }

    private static async Task SpliceAsync(TunnelStream stream, int port)
    {
        await using (stream)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            await using var local = new NetworkStream(socket, ownsSocket: false);
            try
            {
                await Task.WhenAny(stream.CopyToAsync(local), local.CopyToAsync(stream));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }
}
