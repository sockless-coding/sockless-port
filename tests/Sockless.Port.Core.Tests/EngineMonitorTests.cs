using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;

namespace Sockless.Port.Core.Tests;

public sealed class EngineMonitorTests
{
    private sealed class RecordingSink : IEngineEventSink
    {
        public ConcurrentQueue<EndpointState> States { get; } = new();

        public Task StateChangedAsync(EngineConnection connection, CancellationToken cancellationToken)
        {
            States.Enqueue(connection.State);
            return Task.CompletedTask;
        }

        public Task EngineEventAsync(EngineConnection connection, DockerEvent engineEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Unreachable_endpoint_goes_offline_with_a_reason_and_notifies()
    {
        var connections = new EngineConnections();
        var address = new UnixSocketAddress("/nonexistent/sockless-port-monitor.sock");
        connections.Add(new EngineConnection(1, "local", address, EndpointKind.Local));
        var sink = new RecordingSink();
        using var monitor = new EngineMonitor(connections, sink, TimeProvider.System, NullLogger<EngineMonitor>.Instance);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (connections.Get(1)!.State is not EndpointOffline && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        var offline = Assert.IsType<EndpointOffline>(sink.States.First(s => s is EndpointOffline));
        Assert.Contains("/nonexistent/sockless-port-monitor.sock", offline.Reason);
        Assert.True(offline.NextAttempt > offline.Since);
        Assert.Null(connections.Get(1)!.Client);
    }

    [Fact]
    public async Task Endpoints_added_later_are_watched()
    {
        var connections = new EngineConnections();
        var sink = new RecordingSink();
        using var monitor = new EngineMonitor(connections, sink, TimeProvider.System, NullLogger<EngineMonitor>.Instance);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        connections.Add(new EngineConnection(7, "later", new UnixSocketAddress("/nonexistent/later.sock"), EndpointKind.Local));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (sink.States.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sink.States);
    }

    [Theory]
    [InlineData("container", "start", true)]
    [InlineData("container", "die", true)]
    [InlineData("volume", "destroy", true)]
    [InlineData("container", "exec_create: /bin/sh -c healthcheck", false)]
    [InlineData("container", "exec_die", false)]
    [InlineData("container", "health_status: healthy", false)]
    [InlineData("container", "attach", false)]
    [InlineData("container", "resize", false)]
    [InlineData("container", null, false)]
    public void Chatty_events_are_dropped(string type, string? action, bool relevant)
    {
        Assert.Equal(relevant, EngineMonitor.IsRelevant(new DockerEvent { Type = type, Action = action }));
    }

    [Fact]
    public void Missing_socket_message_says_what_to_check()
    {
        var address = new UnixSocketAddress("/var/run/docker.sock");
        var error = new HttpRequestException("connect failed", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied));

        Assert.Contains("group_add", EngineErrors.Describe(error, address));
    }

    [Fact]
    public void Dropped_connection_message_is_plain_language()
    {
        var error = new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)");

        var message = EngineErrors.Describe(error, new TcpAddress("127.0.0.1", 2375));

        Assert.Equal("The engine at tcp://127.0.0.1:2375 closed the connection. It may be restarting or stopped.", message);
    }

    [Fact]
    public async Task Configured_local_engine_wins_over_detection()
    {
        var detector = new LocalEngineDetector(NullLogger<LocalEngineDetector>.Instance);

        var address = await detector.DetectAsync("tcp://127.0.0.1:1", TestContext.Current.CancellationToken);

        Assert.Equal("tcp://127.0.0.1:1", address.ToString());
    }

    [Fact]
    public void Well_known_addresses_cover_docker_and_podman()
    {
        var addresses = LocalEngineDetector.WellKnownAddresses().Select(a => a.ToString()).ToList();

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("npipe:////./pipe/docker_engine", addresses);
            Assert.Contains("npipe:////./pipe/podman-machine-default", addresses);
        }
        else
        {
            Assert.Equal("unix:///var/run/docker.sock", addresses[0]);
            Assert.Contains("unix:///run/podman/podman.sock", addresses);
        }
    }
}
