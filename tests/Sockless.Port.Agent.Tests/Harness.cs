using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Engines;

namespace Sockless.Port.Agent.Tests;

/// <summary>The server's Core services with a running monitor, over a temporary data folder. No web app.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IHostedService _monitor;

    private ServerHarness(ServiceProvider services, IHostedService monitor, string dataPath)
    {
        _services = services;
        _monitor = monitor;
        DataPath = dataPath;
    }

    public string DataPath { get; }

    public IServiceProvider Services => _services;

    public EndpointRegistry Registry => _services.GetRequiredService<EndpointRegistry>();

    public EngineConnections Connections => _services.GetRequiredService<EngineConnections>();

    public static async Task<ServerHarness> StartAsync()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"agent-server-{Guid.NewGuid():n}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Port:DataPath"] = dataPath,
            ["Port:LocalEngine"] = "unix:///nonexistent/sockless-port-agent-tests.sock",
        }).Build();
        var services = new ServiceCollection()
            .AddLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .AddPortCore(configuration)
            .BuildServiceProvider();
        await services.InitializePortAsync(TestContext.Current.CancellationToken);
        var monitor = services.GetServices<IHostedService>().OfType<EngineMonitor>().Single();
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        return new ServerHarness(services, monitor, dataPath);
    }

    /// <summary>Waits until the endpoint's connection satisfies <paramref name="condition"/>.</summary>
    public async Task<EngineConnection> WaitForAsync(int endpointId, Func<EngineConnection, bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        EngineConnection? connection = null;
        while (DateTime.UtcNow < deadline)
        {
            connection = Connections.Get(endpointId);
            if (connection is not null && condition(connection))
                return connection;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Endpoint {endpointId} never got {what}; it's {connection?.State}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TryDelete(DataPath);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort on Windows.
        }
    }
}

/// <summary>An agent running in-process on a loopback port.</summary>
internal sealed class AgentHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AgentHarness(WebApplication app, int port, string dataPath)
    {
        _app = app;
        Port = port;
        DataPath = dataPath;
    }

    public int Port { get; }

    public string DataPath { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="port">0 for any free port; the same port again to restart an agent.</param>
    /// <param name="server">Edge mode: the server address the agent dials.</param>
    public static async Task<AgentHarness> StartAsync(DockerAddress engine, AgentToken? token, string? dataPath = null, int port = 0, Uri? server = null)
    {
        dataPath ??= Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"agent-{Guid.NewGuid():n}");
        var options = new AgentOptions { Engine = engine, Token = token, Port = port, DataPath = dataPath, Server = server };
        var app = AgentApp.Create(options, ["--Logging:LogLevel:Default=Warning"], IPAddress.Loopback);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new AgentHarness(app, new Uri(app.Urls.First()).Port, dataPath);
    }

    public string Address => $"127.0.0.1:{Port}";

    public AgentIdentity Identity => _app.Services.GetRequiredService<AgentIdentity>();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>The server's tunnel endpoint, as the web app maps it, on a loopback port; for edge agents to dial.</summary>
internal sealed class TunnelHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TunnelHost(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>The "server address" edge agents are given.</summary>
    public Uri Address { get; }

    public static async Task<TunnelHost> StartAsync(ServerHarness server)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        var tunnels = server.Services.GetRequiredService<Agents.AgentTunnels>();
        app.Map(Docker.Tunnel.TunnelProtocol.Path, tunnels.HandleAsync);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TunnelHost(app, new Uri(app.Urls.First()));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
