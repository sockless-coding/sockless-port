using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Stacks;

namespace Sockless.Port.Core.Tests;

/// <summary>The stack services over an in-memory database and a temporary data folder, with endpoint 1 in it.</summary>
public sealed class StackHarness : IAsyncDisposable
{
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"stacks-{Guid.NewGuid():n}");

    public PortOptions Options { get; private set; } = null!;

    public RegistryCredentials Registries { get; private set; } = null!;

    public ComposeCli Compose { get; private set; } = null!;

    public StackStore Store { get; private set; } = null!;

    public StackValidator Validator { get; private set; } = null!;

    public StackJobs Jobs { get; private set; } = null!;

    public static async Task<StackHarness> CreateAsync()
    {
        var harness = new StackHarness();
        await harness.InitializeAsync();
        return harness;
    }

    private async Task InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Options = new PortOptions { DataPath = DataPath };
        Directory.CreateDirectory(Options.TempPath);
        Directory.CreateDirectory(Options.StacksPath);

        await _db.OpenAsync(ct);
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            await db.Database.MigrateAsync(ct);
            db.Endpoints.Add(new EngineEndpoint { Name = "local", Address = "unix:///var/run/docker.sock", Kind = EndpointKind.Local });
            await db.SaveChangesAsync(ct);
        }

        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        Registries = new RegistryCredentials(scopes, new EphemeralDataProtectionProvider(), TimeProvider.System, NullLogger<RegistryCredentials>.Instance);
        Compose = new ComposeCli(options, Registries, TimeProvider.System, NullLogger<ComposeCli>.Instance);
        Store = new StackStore(scopes, options, TimeProvider.System);
        Validator = new StackValidator(Compose, options, NullLogger<StackValidator>.Instance);
        Jobs = new StackJobs(Compose, Store, TimeProvider.System, NullLogger<StackJobs>.Instance);
    }

    /// <summary>Skips the test when Compose isn't installed here.</summary>
    public async Task RequireComposeAsync()
    {
        var (tool, problem) = await Compose.StatusAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(tool is null, $"Compose isn't available: {problem}");
    }

    public async ValueTask DisposeAsync()
    {
        Jobs?.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
