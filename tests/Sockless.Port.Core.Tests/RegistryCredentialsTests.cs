using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Registries;

namespace Sockless.Port.Core.Tests;

public sealed class RegistryCredentialsTests : IAsyncLifetime
{
    // One open in-memory connection shared by every scope, so the database outlives each DbContext.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _services = new ServiceCollection()
            .AddDbContext<PortDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PortDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private RegistryCredentials Create(IDataProtectionProvider? protection = null) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), protection ?? new EphemeralDataProtectionProvider(),
            TimeProvider.System, NullLogger<RegistryCredentials>.Instance);

    private static JsonElement Decode(string header) =>
        JsonDocument.Parse(Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/'))).RootElement;

    private async Task<string> StoredPasswordAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<PortDbContext>().Registries.SingleAsync(TestContext.Current.CancellationToken)).ProtectedPassword;
    }

    [Fact]
    public async Task Passwords_are_stored_encrypted_and_attached_by_registry_host()
    {
        var registries = Create();
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await registries.SaveAsync(null, new RegistryInput("Hub", "https://index.docker.io/v1/", "me", "s3cret-token"), ct));
        Assert.Empty(await registries.SaveAsync(null, new RegistryInput("GHCR", "GHCR.io", "bot", "ghp_x"), ct));

        var hub = Decode((await registries.AuthHeaderForAsync("nginx:1.27", ct))!);
        Assert.Equal("me", hub.GetProperty("username").GetString());
        Assert.Equal("s3cret-token", hub.GetProperty("password").GetString());
        Assert.Equal(RegistryAuth.DockerHubServerAddress, hub.GetProperty("serveraddress").GetString());
        Assert.Equal("bot", Decode((await registries.AuthHeaderForAsync("ghcr.io/org/app:1", ct))!).GetProperty("username").GetString());
        Assert.Null(await registries.AuthHeaderForAsync("quay.io/org/app", ct));
        Assert.Null(await registries.AuthHeaderForAsync("sha256:abc", ct));

        var config = Decode((await registries.BuildConfigAsync(ct))!);
        Assert.Equal(["ghcr.io", RegistryAuth.DockerHubServerAddress], config.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task The_database_never_holds_the_password_in_clear()
    {
        await Create().SaveAsync(null, new RegistryInput("Hub", "docker.io", "me", "s3cret-token"), TestContext.Current.CancellationToken);

        var stored = await StoredPasswordAsync();
        Assert.DoesNotContain("s3cret-token", stored);
        Assert.NotEmpty(stored);
    }

    [Fact]
    public async Task Input_is_validated_and_registries_are_unique()
    {
        var registries = Create();
        var ct = TestContext.Current.CancellationToken;

        var errors = await registries.SaveAsync(null, new RegistryInput("", "not a host!", "", null), ct);
        Assert.Equal(["Name", "Password", "Registry", "Username"], errors.Select(e => e.Field).Order());

        Assert.Empty(await registries.SaveAsync(null, new RegistryInput("One", "registry.local:5000", "a", "b"), ct));
        var duplicate = await registries.SaveAsync(null, new RegistryInput("Two", "https://Registry.Local:5000/v2/", "c", "d"), ct);
        Assert.Equal("Registry", Assert.Single(duplicate).Field);
    }

    [Fact]
    public async Task Editing_without_a_password_keeps_the_stored_one()
    {
        var registries = Create();
        var ct = TestContext.Current.CancellationToken;
        await registries.SaveAsync(null, new RegistryInput("Hub", "docker.io", "me", "first"), ct);
        var id = (await registries.ListAsync(ct)).Single().Id;

        Assert.Empty(await registries.SaveAsync(id, new RegistryInput("Renamed", "docker.io", "me2", ""), ct));

        var auth = (await registries.GetAuthAsync(id, ct))!;
        Assert.Equal("me2", auth.Username);
        Assert.Equal("first", auth.Password);
        Assert.Equal("Renamed", (await registries.GetAsync(id, ct))!.Name);
        // The cache was refreshed by the edit.
        Assert.Equal("me2", Decode((await registries.AuthHeaderForAsync("busybox", ct))!).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Credentials_from_lost_keys_are_flagged_and_not_used()
    {
        var ct = TestContext.Current.CancellationToken;
        await Create().SaveAsync(null, new RegistryInput("Hub", "docker.io", "me", "secret"), ct);

        // A new ephemeral provider has different keys, as after losing /data/keys.
        var afterKeyLoss = Create(new EphemeralDataProtectionProvider());

        Assert.False((await afterKeyLoss.ListAsync(ct)).Single().Readable);
        Assert.Null(await afterKeyLoss.AuthHeaderForAsync("nginx", ct));
    }

    [Fact]
    public async Task Removed_credentials_are_no_longer_used()
    {
        var registries = Create();
        var ct = TestContext.Current.CancellationToken;
        await registries.SaveAsync(null, new RegistryInput("Hub", "docker.io", "me", "secret"), ct);
        Assert.NotNull(await registries.AuthHeaderForAsync("nginx", ct));

        Assert.Equal("Hub", await registries.RemoveAsync((await registries.ListAsync(ct)).Single().Id, ct));

        Assert.Null(await registries.AuthHeaderForAsync("nginx", ct));
        Assert.Null(await registries.RemoveAsync(999, ct));
    }
}
