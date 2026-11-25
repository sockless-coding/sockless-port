using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Maintenance;
using Sockless.Port.Registries;
using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

/// <summary>Tests that change the whole engine (prunes), run on their own after the others so they can't remove what another test just made.</summary>
[CollectionDefinition(nameof(EngineWide), DisableParallelization = true)]
public sealed class EngineWide;

/// <summary>Scheduled prunes against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
[Collection(nameof(EngineWide))]
public sealed class PruneIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly string _suffix = Guid.NewGuid().ToString("n")[..10];
    private ServiceProvider _services = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;
    private PruneRunner _runner = null!;

    private string Named => $"sp-test-prune-keep-{_suffix}";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        var ct = TestContext.Current.CancellationToken;
        await _db.OpenAsync(ct);
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PortDbContext>().Database.MigrateAsync(ct);

        var address = DockerAddress.Parse(Address!);
        _client = await DockerClient.ConnectAsync(address, cancellationToken: ct);
        _connection = new EngineConnection(1, "test", address, EndpointKind.Local);
        _connection.SetOnline(_client, EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(ct), _client.ApiVersion), DateTimeOffset.UtcNow);

        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var registries = new RegistryCredentials(scopes, new EphemeralDataProtectionProvider(), TimeProvider.System, NullLogger<RegistryCredentials>.Instance);
        var browser = new VolumeBrowser(Options.Create(new PortOptions { HelperImage = Image }), registries, TimeProvider.System, NullLogger<VolumeBrowser>.Instance);
        _runner = new PruneRunner(browser, new MaintenanceLog(scopes, NullLogger<MaintenanceLog>.Instance),
            new AuditLog(scopes, TimeProvider.System, NullLogger<AuditLog>.Instance), TimeProvider.System, NullLogger<PruneRunner>.Instance);

        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try { await _client.RemoveVolumeAsync(Named, force: true); } catch (DockerApiException) { }
        _client.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_prune_removes_unused_networks_and_anonymous_volumes_but_keeps_named_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var network = $"sp-test-prune-net-{_suffix}";
        await _client.CreateNetworkAsync(new NetworkCreateRequest { Name = network }, ct);
        await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = Named }, ct);
        // An anonymous volume: one a container declared without naming it, left behind when the container goes.
        var container = await _client.CreateContainerAsync($"sp-test-prune-{_suffix}", new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray("true"),
            ["Volumes"] = new JsonObject { ["/data"] = new JsonObject() },
        }, ct);
        var (inspect, _) = await _client.InspectContainerAsync(container.Id, ct);
        var anonymous = Assert.Single(inspect.Mounts ?? [], m => m.Destination == "/data").Name!;
        await _client.RemoveContainerAsync(container.Id, force: true, cancellationToken: ct);

        var canKeepNamed = _client.CanPruneAnonymousVolumesOnly;
        var result = await _runner.RunAsync(_connection, _client,
            new PruneOptions(PruneImages.Dangling, 0, canKeepNamed ? PruneVolumes.Anonymous : PruneVolumes.None, Networks: true, BuildCache: true),
            MaintenanceTrigger.Schedule, ct);

        Assert.True(result.Outcome == MaintenanceOutcome.Succeeded, result.Summary);
        Assert.Contains(network, result.Summary);
        Assert.DoesNotContain(await _client.ListNetworksAsync(ct), n => n.Name == network);
        var volumes = await _client.ListVolumesAsync(ct);
        Assert.Contains(volumes, v => v.Name == Named);
        if (canKeepNamed)
        {
            Assert.Contains(anonymous, result.Summary);
            Assert.DoesNotContain(volumes, v => v.Name == anonymous);
        }
        else
        {
            await _client.RemoveVolumeAsync(anonymous, force: true, ct);
        }
    }
}
