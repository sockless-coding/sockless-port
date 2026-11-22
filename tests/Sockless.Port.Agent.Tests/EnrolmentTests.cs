using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Agents;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Engines;

namespace Sockless.Port.Agent.Tests;

/// <summary>
/// The whole direct-mode flow on loopback: the server enrols an agent from its install command's
/// token, then reaches a (fake) engine through it over mutual TLS.
/// </summary>
public sealed class EnrolmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Server_enrols_the_agent_and_relays_the_engine_over_mutual_tls()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();

        // The server creates the endpoint first; the agent's port is only known once it listens,
        // so start it with the token that endpoint got and point the endpoint at it.
        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("remote", "127.0.0.1:1"), Ct);
        Assert.Empty(errors);
        var token = server.Connections.Get(id!.Value)!.Enrolment!.Token;
        await using var agent = await AgentHarness.StartAsync(engine.Address, token);
        await PointAtAsync(server, id.Value, agent.Address, token);

        var connection = await server.WaitForAsync(id.Value, c => c.State is EndpointOnline, "online");

        Assert.Equal("Docker 29.0.0", connection.Engine!.DisplayName);
        Assert.NotNull(connection.Agent!.Thumbprint);
        Assert.NotNull(connection.Agent.EnrolledAt);
        Assert.Equal(AgentApp.Version, connection.Agent.Info?.Version);
        Assert.Equal(engine.Address.ToString(), connection.Agent.Info?.Engine);
        Assert.Null(connection.Enrolment);
        Assert.True(File.Exists(Path.Combine(agent.DataPath, AgentIdentity.CaFile)));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<PortDbContext>().Endpoints.SingleAsync(e => e.Id == id, Ct);
            Assert.Null(row.EnrolmentTokenId);
            Assert.Null(row.ProtectedEnrolmentSecret);
            Assert.Equal(connection.Agent.Thumbprint, row.AgentThumbprint);
            Assert.NotNull(row.EnrolledAt);
        }

        // Ordinary requests and a hijacked exec both pass through.
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
    }

    [Fact]
    public async Task Only_the_server_certificate_gets_through()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var (id, agent) = await EnrolAsync(server, engine);
        await using var _ = agent;

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var anonymous = new HttpClient(handler);
        using var refused = await anonymous.GetAsync($"https://{agent.Address}/version", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Only the Sockless Port server", await refused.Content.ReadAsStringAsync(Ct));

        // The health check needs no certificate and says nothing else.
        Assert.Equal("ok", await anonymous.GetStringAsync($"https://{agent.Address}{AgentProtocol.HealthPath}", Ct));
    }

    [Fact]
    public async Task Agent_keeps_its_identity_across_restarts()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var (id, agent) = await EnrolAsync(server, engine);
        var port = agent.Port;
        var dataPath = agent.DataPath;
        await agent.DisposeAsync();

        await server.WaitForAsync(id, c => c.State is EndpointOffline, "offline");

        // Restarted without a token, as the update command does.
        await using var restarted = await AgentHarness.StartAsync(engine.Address, token: null, dataPath, port);
        var connection = await server.WaitForAsync(id, c => c.State is EndpointOnline, "online again", TimeSpan.FromSeconds(60));
        Assert.NotNull(connection.Agent?.Info);
    }

    [Fact]
    public async Task An_agent_started_with_another_token_is_not_enrolled()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var agent = await AgentHarness.StartAsync(engine.Address, AgentToken.Create());

        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("remote", agent.Address), Ct);
        Assert.Empty(errors);

        var connection = await server.WaitForAsync(id!.Value, c => c.State is EndpointEnrolling { Problem: not null }, "an enrolment problem");
        Assert.Contains("different enrolment command", ((EndpointEnrolling)connection.State).Problem);
        Assert.Null(connection.Agent!.Thumbprint);
        Assert.False(File.Exists(Path.Combine(agent.DataPath, AgentIdentity.CaFile)));
    }

    [Fact]
    public async Task Enrolling_again_revokes_the_old_certificate()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var (id, agent) = await EnrolAsync(server, engine);
        await using var _ = agent;
        var oldPin = server.Connections.Get(id)!.Agent!.Thumbprint;

        Assert.Empty(await server.Registry.ReenrolAsync(id, newAddress: null, Ct));

        // The old agent still holds its identity, so it refuses the new enrolment, and the server no longer accepts it.
        var connection = await server.WaitForAsync(id, c => c.State is EndpointEnrolling { Problem: not null }, "an enrolment problem");
        Assert.Contains("already enrolled", ((EndpointEnrolling)connection.State).Problem);
        Assert.Null(connection.Agent!.Thumbprint);
        Assert.NotNull(oldPin);
        Assert.Null(connection.Client);
    }

    [Fact]
    public async Task A_port_without_an_agent_says_so()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();

        // The fake engine speaks plain HTTP: not an agent.
        var (id, _) = await server.Registry.AddAgentAsync(new AgentEndpointInput("plain", $"127.0.0.1:{engine.Address.Port}"), Ct);
        var connection = await server.WaitForAsync(id!.Value, c => c.State is EndpointEnrolling { Problem: not null }, "an enrolment problem");
        Assert.Contains("isn't a Sockless Port agent", ((EndpointEnrolling)connection.State).Problem);
    }

    [Fact]
    public async Task Removing_an_endpoint_stops_watching_it()
    {
        await using var engine = await FakeEngine.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var (id, agent) = await EnrolAsync(server, engine);
        await using var _ = agent;

        Assert.Equal("remote", await server.Registry.RemoveAsync(id, Ct));
        Assert.Null(server.Connections.Get(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.Registry.RemoveAsync(server.Connections.All.Single(c => c.IsLocal).Id, Ct));
    }

    /// <summary>Starts an agent with a new endpoint's token and waits until the server has it online.</summary>
    private static async Task<(int Id, AgentHarness Agent)> EnrolAsync(ServerHarness server, FakeEngine engine)
    {
        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("remote", "127.0.0.1:1"), Ct);
        Assert.Empty(errors);
        var token = server.Connections.Get(id!.Value)!.Enrolment!.Token;
        var agent = await AgentHarness.StartAsync(engine.Address, token);
        await PointAtAsync(server, id.Value, agent.Address, token);
        await server.WaitForAsync(id.Value, c => c.State is EndpointOnline, "online");
        return (id.Value, agent);
    }

    /// <summary>
    /// Points the endpoint at the agent's real port while keeping the token the agent was started
    /// with (a re-enrolment would issue a new one).
    /// </summary>
    internal static async Task PointAtAsync(ServerHarness server, int id, string address, AgentToken token)
    {
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            var row = await db.Endpoints.SingleAsync(e => e.Id == id, Ct);
            row.Address = $"https://{address}";
            await db.SaveChangesAsync(Ct);
        }
        var connection = server.Connections.Get(id)!;
        var tcp = (TcpAddress)DockerAddress.Parse($"https://{address}");
        connection.SetAgent(tcp, new AgentStatus(null, null, null));
        connection.SetEnrolment(new PendingEnrolment(token, connection.Enrolment!.ExpiresAt));
        server.Connections.Reconnect(connection);
    }
}
