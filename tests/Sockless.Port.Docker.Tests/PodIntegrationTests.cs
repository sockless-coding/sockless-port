using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Tests;

/// <summary>
/// Pods through libpod against a real engine: the whole life of a pod on Podman, and a clean 404 on
/// Docker. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PodIntegrationTests : IAsyncLifetime
{
    // Fully qualified: Podman may have no search registries for short names.
    private const string Image = "docker.io/library/busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");

    private readonly string _name = $"sp-pod-{Guid.NewGuid():n}"[..20];
    private DockerClient _client = null!;
    private EngineDescriptor _engine = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: Ct);
        _engine = EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(Ct), _client.ApiVersion);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        if (_engine?.Has(EngineCapabilities.Pods) == true)
        {
            try { await _client.RemovePodAsync(_name); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    [Fact]
    public async Task Docker_has_no_libpod_api()
    {
        Assert.SkipWhen(_engine.Has(EngineCapabilities.Pods), "The test engine has pods.");

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => _client.ListPodsAsync(Ct));
        Assert.True(ex.IsNotFound);
    }

    [Fact]
    public async Task A_pod_is_created_started_stopped_and_removed_with_its_containers()
    {
        Assert.SkipUnless(_engine.Has(EngineCapabilities.Pods), "The test engine has no pods.");
        if (!await _client.ImageExistsAsync(Image, Ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: Ct)) { }
        }

        var podId = await _client.CreatePodAsync(new PodCreateRequest { Name = _name, Labels = new Dictionary<string, string> { ["team"] = "infra" } }, Ct);
        var appId = await _client.CreatePodContainerAsync(new PodContainerCreateRequest { Name = $"{_name}-app", Image = Image, Pod = podId, Command = ["sleep", "300"] }, Ct);

        await _client.StartPodAsync(_name, Ct);
        await _client.StartPodAsync(_name, Ct); // already running: 304, not an error

        var listed = Assert.Single(await _client.ListPodsAsync(Ct), p => p.Id == podId);
        Assert.Equal(PodStates.Running, listed.Status);
        Assert.Equal("infra", listed.Labels!["team"]);
        var app = Assert.Single(listed.Workloads);
        Assert.Equal((appId, $"{_name}-app", "running"), (app.Id, app.DisplayName, app.Status));
        Assert.Contains(listed.Containers!, c => listed.IsInfra(c.Id));

        var (pod, json) = await _client.InspectPodAsync(podId, Ct);
        Assert.Equal(_name, pod.Name);
        Assert.Contains(podId, json, StringComparison.Ordinal);
        Assert.Contains("net", pod.SharedNamespaces!);

        // Docker's container list shows the pod's containers too, but doesn't say they're in one.
        var containers = await _client.ListContainersAsync(all: true, Ct);
        Assert.Contains(containers, c => c.Id == appId);
        Assert.Contains(containers, c => c.Id == listed.InfraId);

        await _client.StopPodAsync(_name, timeout: 1, cancellationToken: Ct);
        await _client.StopPodAsync(_name, timeout: 1, cancellationToken: Ct); // already stopped: 304
        Assert.False(PodStates.IsActive((await _client.InspectPodAsync(podId, Ct)).Pod.State));

        await _client.RestartPodAsync(_name, Ct);
        Assert.Equal(PodStates.Running, (await _client.InspectPodAsync(podId, Ct)).Pod.State);

        // Forced, so a running pod goes with all its containers.
        var removed = await _client.RemovePodAsync(_name, Ct);
        Assert.Contains(appId, removed.RemovedCtrs!.Keys);
        Assert.DoesNotContain(await _client.ListPodsAsync(Ct), p => p.Id == podId);
        Assert.DoesNotContain(await _client.ListContainersAsync(all: true, Ct), c => c.Id == appId);
    }

    [Fact]
    public async Task A_pod_whose_container_cant_start_reports_it()
    {
        Assert.SkipUnless(_engine.Has(EngineCapabilities.Pods), "The test engine has no pods.");
        if (!await _client.ImageExistsAsync(Image, Ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: Ct)) { }
        }

        var podId = await _client.CreatePodAsync(new PodCreateRequest { Name = _name }, Ct);
        await _client.CreatePodContainerAsync(new PodContainerCreateRequest { Name = $"{_name}-broken", Image = Image, Pod = podId, Command = ["/nope"] }, Ct);

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => _client.StartPodAsync(_name, Ct));

        Assert.True(ex.IsConflict);
        Assert.Contains("/nope", ex.Message, StringComparison.Ordinal);
    }
}
