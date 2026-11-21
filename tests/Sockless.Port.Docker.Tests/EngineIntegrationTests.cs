using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Tests;

/// <summary>
/// Runs against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set to an engine
/// address. <c>SOCKLESS_PORT_TEST_EXPECT</c> (<c>docker</c>, <c>swarm</c> or <c>podman</c>) states
/// which kind of engine it is, so detection is checked too. CI runs this against each kind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EngineIntegrationTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private static readonly string? Expect = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_EXPECT")?.ToLowerInvariant();

    private DockerClient _client = null!;
    private readonly List<string> _volumes = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var name in _volumes)
        {
            try { await _client.RemoveVolumeAsync(name, force: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    [Fact]
    public async Task Detects_the_expected_engine()
    {
        var info = await _client.GetInfoAsync(TestContext.Current.CancellationToken);
        var engine = EngineDescriptor.Detect(_client.ServerVersion, info, _client.ApiVersion);

        Assert.InRange(_client.ApiVersion, DockerClient.MinimumApiVersion, DockerClient.MaximumApiVersion);
        switch (Expect)
        {
            case "docker":
                Assert.Equal(EngineKind.Docker, engine.Kind);
                Assert.False(engine.Has(EngineCapabilities.Swarm));
                break;
            case "swarm":
                Assert.Equal(EngineKind.Docker, engine.Kind);
                Assert.True(engine.Has(EngineCapabilities.SwarmManager));
                break;
            case "podman":
                Assert.Equal(EngineKind.Podman, engine.Kind);
                Assert.True(engine.Has(EngineCapabilities.Pods));
                break;
        }
    }

    [Fact]
    public async Task Lists_resources_and_reads_disk_usage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _client.ListContainersAsync(all: true, ct);
        await _client.ListImagesAsync(ct);
        await _client.ListVolumesAsync(ct);
        Assert.NotEmpty(await _client.ListNetworksAsync(ct));
        Assert.NotNull(await _client.GetDiskUsageAsync(ct));
    }

    [Fact]
    public async Task Volume_round_trip_emits_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"sockless-port-test-{Guid.NewGuid():n}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var seen = new TaskCompletionSource<DockerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Task.Run(async () =>
        {
            try
            {
                var events = _client.MonitorEventsAsync(new Dictionary<string, string[]> { ["type"] = ["volume"] }, cts.Token);
                await using var enumerator = events.GetAsyncEnumerator(cts.Token);
                var moveNext = enumerator.MoveNextAsync();
                subscribed.TrySetResult();
                while (await moveNext)
                {
                    if (enumerator.Current.Actor?.Id == name)
                    {
                        seen.TrySetResult(enumerator.Current);
                        return;
                    }
                    moveNext = enumerator.MoveNextAsync();
                }
            }
            catch (Exception ex)
            {
                seen.TrySetException(ex);
            }
        }, cts.Token);

        await subscribed.Task;
        await Task.Delay(500, ct); // let the engine register the subscription before acting

        _volumes.Add(name);
        var created = await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = name, Labels = new Dictionary<string, string> { ["test"] = "sockless-port" } }, ct);
        Assert.Equal(name, created.Name);
        Assert.Contains(await _client.ListVolumesAsync(ct), v => v.Name == name);

        var evt = await seen.Task.WaitAsync(cts.Token);
        Assert.Equal("volume", evt.Type);
        Assert.Equal("create", evt.Action);

        await _client.RemoveVolumeAsync(name, cancellationToken: ct);
        _volumes.Remove(name);
        await cts.CancelAsync();
        await watch.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Fact]
    public async Task Missing_volume_is_not_found()
    {
        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            _client.RemoveVolumeAsync($"missing-{Guid.NewGuid():n}", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.IsNotFound);
    }
}
