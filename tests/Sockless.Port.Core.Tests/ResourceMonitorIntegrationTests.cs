using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;

namespace Sockless.Port.Core.Tests;

/// <summary>The Resources page's sampler against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
public sealed class ResourceMonitorIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private DockerClient _client = null!;
    private readonly List<string> _cleanup = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
        if (!await _client.ImageExistsAsync(Image, TestContext.Current.CancellationToken))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: TestContext.Current.CancellationToken)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var id in _cleanup)
        {
            try { await _client.RemoveContainerAsync(id, force: true, removeVolumes: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    [Fact]
    public async Task A_running_container_is_measured()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"sp-usage-{Guid.NewGuid():n}"[..20];
        var (body, errors) = ContainerSpecBuilder.Build(new ContainerSpec { Image = Image, Command = "sleep 300" });
        Assert.Empty(errors);
        var id = (await _client.CreateContainerAsync(name, body!, ct)).Id;
        _cleanup.Add(id);
        await _client.StartContainerAsync(id, ct);

        var info = await _client.GetInfoAsync(ct);
        var connection = new EngineConnection(1, "test", DockerAddress.Parse(Address!), EndpointKind.Local);
        connection.SetOnline(_client, EngineDescriptor.Detect(_client.ServerVersion, info, _client.ApiVersion), DateTimeOffset.UtcNow);
        using var monitor = new ResourceMonitor(TimeProvider.System, NullLogger<ResourceMonitor>.Instance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        ContainerUsage? usage = null;
        await foreach (var snapshot in monitor.WatchAsync(connection, timeout.Token))
        {
            Assert.Null(snapshot.Error);
            usage = snapshot.Containers.FirstOrDefault(c => c.Id == id);
            if (usage is { Measured: true } or { Error: not null })
                break;
        }

        Assert.NotNull(usage);
        Assert.Equal(name, usage.Name);
        if (usage.Error is { } error && error.Contains("cgroup", StringComparison.OrdinalIgnoreCase))
        {
            // Podman without cgroup access (e.g. nested in a container) can't report stats; the page says so per row.
            Assert.Skip($"Engine can't report stats here: {error}");
        }
        Assert.Null(usage.Error);
        Assert.True(usage.Memory > 0);
        Assert.True(info.NCpu > 0);
    }
}
