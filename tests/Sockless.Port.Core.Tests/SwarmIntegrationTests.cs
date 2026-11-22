using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;

namespace Sockless.Port.Core.Tests;

/// <summary>
/// Swarm stacks (<c>docker stack</c>) and service writes against a real swarm manager. Skipped
/// unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is a swarm manager and the Docker CLI is installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SwarmIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sptest{Guid.NewGuid():n}"[..14];
    private StackHarness _harness = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        var address = DockerAddress.Parse(Address!);
        _client = await DockerClient.ConnectAsync(address, cancellationToken: Ct);
        var engine = EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(Ct), _client.ApiVersion);
        Assert.SkipUnless(engine.Has(EngineCapabilities.SwarmManager), "The test engine isn't a swarm manager.");
        _harness = await StackHarness.CreateAsync();
        await _harness.RequireComposeAsync();
        await _harness.RequireDockerCliAsync();
        _connection = new EngineConnection(1, "test", address, EndpointKind.Local);
        _connection.SetOnline(_client, engine, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null && _connection is not null)
        {
            try
            {
                await _harness.Docker.RunAsync(new DockerCliRequest(["stack", "rm", _name], _connection.Address));
            }
            catch (Exception ex) when (ex is ComposeUnavailableException or IOException)
            {
            }
            foreach (var service in await _client.ListServicesAsync(new Dictionary<string, string[]> { ["name"] = [_name] }))
            {
                try { await _client.RemoveServiceAsync(service.Id); } catch (DockerApiException) { }
            }
        }
        _client?.Dispose();
        if (_harness is not null)
            await _harness.DisposeAsync();
    }

    private static async Task<StackJobView> EndOfAsync(StackJob job)
    {
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), Ct))
        {
        }
        return job.View();
    }

    private Task<IReadOnlyList<SwarmService>> StackServicesAsync() =>
        _client.ListServicesAsync(new Dictionary<string, string[]> { ["label"] = [$"{ServiceSpec.StackNamespaceLabel}={_name}"] }, Ct);

    private const string Compose = """
        services:
          api:
            image: busybox:1.37
            command: ["sleep", "600"]
            environment:
              GREETING: ${GREETING:-unset}
            deploy:
              replicas: 2
          worker:
            image: busybox:1.37
            command: ["sleep", "600"]
            deploy:
              mode: global
        """;

    [Fact]
    public async Task A_swarm_stack_deploys_from_its_files_and_is_removed_from_the_swarm()
    {
        var content = StackContent.Normalize(Compose, "GREETING=from-env-file\n");
        await _harness.Store.CreateAsync(1, _name, content, adopted: false, StackKind.Swarm, Ct);

        var deploy = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Deploy));
        Assert.True(deploy.Status is StackJobSucceeded, string.Join('\n', deploy.Log));
        Assert.Contains(deploy.Log, l => l.Contains("stack deploy --compose-file compose.yaml", StringComparison.Ordinal));

        var services = (await StackServicesAsync()).ToDictionary(s => s.Name);
        Assert.Equal([$"{_name}_api", $"{_name}_worker"], services.Keys.Order());
        Assert.Equal(2UL, services[$"{_name}_api"].Spec!.Mode!.Replicated!.Replicas);
        Assert.True(services[$"{_name}_worker"].IsGlobal);
        // .env reaches the interpolation, as with Compose.
        Assert.Contains("GREETING=from-env-file", services[$"{_name}_api"].Spec!.TaskTemplate!.ContainerSpec!.Env!);
        Assert.NotNull((await _harness.Store.GetAsync(1, _name, Ct))!.DeployedAt);

        // Swarm stacks don't stop.
        await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(_connection, _name, StackOperation.Stop));

        var forceBefore = services[$"{_name}_api"].Spec!.TaskTemplate!.ForceUpdate;
        var restart = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Restart));
        Assert.True(restart.Status is StackJobSucceeded, string.Join('\n', restart.Log));
        Assert.Equal(forceBefore + 1, (await StackServicesAsync()).Single(s => s.Name == $"{_name}_api").Spec!.TaskTemplate!.ForceUpdate);

        // Removing a service from the file removes it from the swarm (--prune).
        await _harness.Store.SaveAsync(1, _name, StackContent.Normalize(Compose[..Compose.IndexOf("  worker:", StringComparison.Ordinal)], ""), Ct);
        var redeploy = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Update));
        Assert.True(redeploy.Status is StackJobSucceeded, string.Join('\n', redeploy.Log));
        services = (await StackServicesAsync()).ToDictionary(s => s.Name);
        Assert.Equal([$"{_name}_api"], services.Keys);
        Assert.Contains("GREETING=unset", services[$"{_name}_api"].Spec!.TaskTemplate!.ContainerSpec!.Env!);

        var down = await EndOfAsync(await _harness.Jobs.StartAsync(_connection, _name, StackOperation.Down));
        Assert.True(down.Status is StackJobSucceeded, string.Join('\n', down.Log));
        Assert.Empty(await StackServicesAsync());
        Assert.Null((await _harness.Store.GetAsync(1, _name, Ct))!.DeployedAt);
    }

    [Fact]
    public async Task Swarm_validation_uses_docker_stack_config_and_warns_about_ignored_settings()
    {
        var folder = _harness.Store.Files.Directory(1, _name);

        var ignored = await _harness.Validator.CheckAsync(_name, StackContent.Normalize(
            "services:\n  web:\n    image: busybox:1.37\n    container_name: fixed\n    restart: always\n    deploy:\n      labels:\n        a: b\n", ""), folder, StackKind.Swarm, cancellationToken: Ct);
        Assert.True(ignored.Valid, string.Join('\n', ignored.Errors.Select(e => e.Message)));
        Assert.Contains(ignored.Warnings, w => w.StartsWith("web sets container_name:", StringComparison.Ordinal));
        Assert.Contains(ignored.Warnings, w => w.StartsWith("web sets restart:", StringComparison.Ordinal));
        // Compose's "only applied to Swarm services" is noise for a swarm stack.
        Assert.DoesNotContain(ignored.Warnings, w => w.Contains("Swarm", StringComparison.Ordinal));

        // Compose takes pull_policy; swarm's schema doesn't.
        var file = StackContent.Normalize("services:\n  web:\n    image: busybox:1.37\n    pull_policy: always\n", "");
        Assert.True((await _harness.Validator.CheckAsync(_name, file, folder, StackKind.Compose, cancellationToken: Ct)).Valid);
        var swarm = await _harness.Validator.CheckAsync(_name, file, folder, StackKind.Swarm, cancellationToken: Ct);
        Assert.False(swarm.Valid);
        Assert.StartsWith("Swarm: ", swarm.Errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("pull_policy", swarm.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_services_are_pinned_to_the_digest_their_tag_points_at()
    {
        var services = new SwarmServices(_harness.Registries, NullLogger<SwarmServices>.Instance);
        var (spec, errors) = ServiceSpecBuilder.Build(new ServiceForm { Name = _name, Image = Image, Replicas = 0, Command = "sleep 600" }, ServiceReferences.Empty);
        Assert.Empty(errors);

        var created = await services.CreateAsync(_client, spec!, Ct);
        try
        {
            var (service, _) = await _client.InspectServiceAsync(created.Id, Ct);
            var image = service.Spec!.TaskTemplate!.ContainerSpec!.Image!;
            if (created.Warnings.Any(w => w.StartsWith("Couldn't ask the registry", StringComparison.Ordinal)))
                Assert.Equal(Image, image);
            else
                Assert.Matches(@"^busybox:1\.37@sha256:[0-9a-f]{64}$", image);
            Assert.Equal(Image, service.Image);

            // Nothing to roll back to before the first update.
            await Assert.ThrowsAsync<ServiceRejectedException>(() => services.RollbackAsync(_client, created.Id, Ct));

            // Scaling keeps the spec, digest included.
            await services.ScaleAsync(_client, created.Id, 1, Ct);
            var (scaled, _) = await _client.InspectServiceAsync(created.Id, Ct);
            Assert.Equal(1UL, scaled.Spec!.Mode!.Replicated!.Replicas);
            Assert.Equal(image, scaled.Spec.TaskTemplate!.ContainerSpec!.Image);
            Assert.NotNull(scaled.PreviousSpec);
        }
        finally
        {
            await _client.RemoveServiceAsync(created.Id, Ct);
        }
    }
}
