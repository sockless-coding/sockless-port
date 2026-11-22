using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Containers;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;

namespace Sockless.Port.Core.Tests;

/// <summary>Update checks of containers against a real engine and Docker Hub. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
public sealed class ImageUpdatesIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly string _name = $"sp-updates-{Guid.NewGuid():n}"[..24];
    private ServiceProvider _services = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;
    private ImageUpdates _updates = null!;

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
        var registries = new RegistryCredentials(_services.GetRequiredService<IServiceScopeFactory>(), new EphemeralDataProtectionProvider(),
            TimeProvider.System, NullLogger<RegistryCredentials>.Instance);
        _updates = new ImageUpdates(registries, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try { await _client.RemoveContainerAsync(_name, force: true); } catch (DockerApiException) { }
        _client.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_container_is_checked_by_its_reference_even_when_listed_by_image_id()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
        var (body, errors) = ContainerSpecBuilder.Build(new ContainerSpec { Image = Image, Command = "sleep 300", Network = "bridge" });
        Assert.Empty(errors);
        var id = (await _client.CreateContainerAsync(_name, body!, ct)).Id;
        var listed = Assert.Single(await _client.ListContainersAsync(new Dictionary<string, string[]> { ["name"] = [_name] }, all: true, ct));

        // Docker lists a container by image ID once its tag points at another image (after a pull).
        var byId = listed with { Image = listed.ImageId };
        var summary = await _updates.CheckContainersAsync(_connection, _client, [byId], ct);

        Assert.Equal(1, summary.Checked);
        Assert.Equal(0, summary.Unknown);
        Assert.True(_updates.ForContainer(1, byId) is UpToDate or UpdateAvailable);
        Assert.Same(_updates.ForContainer(1, byId), _updates.ForContainer(1, listed));
        Assert.True(await _updates.CheckContainerAsync(_connection, _client, id, ct) is UpToDate or UpdateAvailable);
    }
}
