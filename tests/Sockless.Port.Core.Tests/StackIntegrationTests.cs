using System.Text.RegularExpressions;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Core.Tests;

/// <summary>
/// Stack jobs running Compose against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c>
/// is set and Compose is installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StackIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sp-test-{Guid.NewGuid():n}"[..16];
    private StackHarness _harness = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;

    private static string Compose(string image = Image) =>
        $"services:\n  app:\n    image: {image}\n    command: [\"sleep\", \"600\"]\n    network_mode: bridge\n    volumes:\n      - data:/data\n    environment:\n      GREETING: ${{GREETING}}\nvolumes:\n  data: {{}}\n";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _harness = await StackHarness.CreateAsync();
        await _harness.RequireComposeAsync();

        var ct = TestContext.Current.CancellationToken;
        var address = DockerAddress.Parse(Address!);
        _client = await DockerClient.ConnectAsync(address, cancellationToken: ct);
        _connection = new EngineConnection(1, "test", address, isLocal: true);
        _connection.SetOnline(_client, EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(ct), _client.ApiVersion), DateTimeOffset.UtcNow);
        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
        {
            if (_harness is not null)
                await _harness.DisposeAsync();
            return;
        }
        try
        {
            await _harness.Compose.RunAsync(new ComposeRequest(_name, ["down", "--volumes", "--remove-orphans"], _connection.Address));
        }
        catch (Exception ex) when (ex is ComposeUnavailableException or IOException)
        {
        }
        _client.Dispose();
        await _harness.DisposeAsync();
    }

    /// <summary>Waits for the job to end, then its whole log (a watcher's last update has only the newest lines).</summary>
    private static async Task<StackJobView> EndOfAsync(StackJob job)
    {
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken))
        {
        }
        return job.View();
    }

    private async Task<ComposeProject> ProjectAsync()
    {
        var containers = await _client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{ContainerLabels.ComposeProject}={_name}"] }, all: true, TestContext.Current.CancellationToken);
        return StackInventory.Projects(containers).GetValueOrDefault(_name) ?? ComposeProject.Empty(_name);
    }

    [Fact]
    public async Task A_stack_deploys_stops_and_goes_down_from_its_files()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = StackContent.Normalize(Compose(), "GREETING=hello");
        await _harness.Store.CreateAsync(1, _name, content, adopted: false, ct);

        var deploy = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Deploy));
        Assert.True(deploy.Status is StackJobSucceeded, string.Join('\n', deploy.Log));
        Assert.StartsWith("$ ", deploy.Log[0], StringComparison.Ordinal);
        Assert.Equal(content, await _harness.Store.Files.ReadDeployedAsync(1, _name, ct));
        Assert.NotNull((await _harness.Store.GetAsync(1, _name, ct))!.DeployedAt);

        var project = await ProjectAsync();
        Assert.Equal(StackState.Running, project.State);
        Assert.Equal(["app"], project.Services.Select(s => s.Name));
        var (inspect, _) = await _client.InspectContainerAsync(project.Containers[0].Id, ct);
        Assert.Contains("GREETING=hello", inspect.Config!.Env!);

        // A second deploy while one runs is refused.
        var redeploy = await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Deploy);
        await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(_connection, _name, StackOperation.Stop));
        Assert.IsType<StackJobSucceeded>((await EndOfAsync(redeploy)).Status);

        var stop = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Stop));
        Assert.IsType<StackJobSucceeded>(stop.Status);
        Assert.Equal(StackState.Stopped, (await ProjectAsync()).State);

        var down = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Down, new StackJobOptions(RemoveVolumes: true)));
        Assert.IsType<StackJobSucceeded>(down.Status);
        Assert.Equal(StackState.Down, (await ProjectAsync()).State);
        Assert.Null((await _harness.Store.GetAsync(1, _name, ct))!.DeployedAt);
        Assert.DoesNotContain(await _client.ListVolumesAsync(ct), v => v.Name == $"{_name}_data");

        var remove = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Remove));
        Assert.IsType<StackJobSucceeded>(remove.Status);
        Assert.Null(await _harness.Store.GetAsync(1, _name, ct));
        Assert.Same(remove.Job, _harness.Jobs.Latest(1, _name));
    }

    [Fact]
    public async Task A_failed_deploy_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        await _harness.Store.CreateAsync(1, _name, StackContent.Normalize(Compose("sockless-port-test/does-not-exist:1"), ""), adopted: false, ct);

        var end = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Deploy));

        var failed = Assert.IsType<StackJobFailed>(end.Status);
        Assert.StartsWith("docker compose up failed", failed.Error, StringComparison.Ordinal);
        Assert.NotEmpty(failed.Hints);
        Assert.Null((await _harness.Store.GetAsync(1, _name, ct))!.DeployedAt);
    }

    [Fact]
    public async Task A_project_deployed_elsewhere_is_managed_by_name_and_can_be_adopted()
    {
        var ct = TestContext.Current.CancellationToken;
        // Deployed "by hand" from a folder outside Sockless Port's stacks.
        var elsewhere = Directory.CreateTempSubdirectory("sp-elsewhere").FullName;
        try
        {
            await new StackFiles(elsewhere).WriteAsync(0, "p", StackContent.Normalize(Compose(), "GREETING=outside"), ct);
            var folder = new StackFiles(elsewhere).Directory(0, "p");
            var up = await _harness.Compose.RunAsync(new ComposeRequest(_name, ["up", "--detach"], _connection.Address, folder), cancellationToken: ct);
            Assert.True(up.Succeeded, string.Join('\n', up.Output));

            await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(_connection, _name, StackOperation.Deploy));
            Assert.IsType<StackJobSucceeded>((await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Stop))).Status);
            Assert.IsType<StackJobSucceeded>((await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Start))).Status);
            Assert.Equal(StackState.Running, (await ProjectAsync()).State);

            // This test runs where Compose ran, so the project's own file is readable.
            var adopted = await StackAdopter.AdoptAsync(_client, await ProjectAsync(), ct);
            Assert.NotNull(adopted.Source);
            Assert.Contains("GREETING=outside", adopted.Content.Env, StringComparison.Ordinal);

            // Rebuilt from the containers, as when the file isn't reachable.
            var project = await ProjectAsync();
            var container = project.Containers[0];
            var (inspect, json) = await _client.InspectContainerAsync(container.Id, ct);
            var image = (await _client.InspectImageAsync(inspect.Image!, ct)).Image.Config;
            var rebuilt = StackAdopter.Rebuild(_name, [new StackAdopter.ServiceSource("app", inspect, System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject(), image)]);
            // Podman records the fully qualified name (docker.io/library/busybox:1.37).
            Assert.Matches($"image: \"(docker.io/library/)?{Regex.Escape(Image)}\"", rebuilt);
            Assert.Contains("GREETING: \"outside\"", rebuilt, StringComparison.Ordinal);
            Assert.Contains("- \"data:/data\"", rebuilt, StringComparison.Ordinal);
            var check = await _harness.Validator.CheckAsync(_name, new StackContent(rebuilt, ""), "/x", cancellationToken: ct);
            Assert.True(check.Valid, string.Join('\n', check.Errors.Select(e => e.Message)));

            Assert.IsType<StackJobSucceeded>((await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Down,
                new StackJobOptions(RemoveVolumes: true)))).Status);
            Assert.Empty((await ProjectAsync()).Containers);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}
