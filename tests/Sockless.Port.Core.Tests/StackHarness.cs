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

    public DockerCli Docker { get; private set; } = null!;

    public StackStore Store { get; private set; } = null!;

    public StackValidator Validator { get; private set; } = null!;

    public StackJobs Jobs { get; private set; } = null!;

    public GitCli Git { get; private set; } = null!;

    public StackGit StackGit { get; private set; } = null!;

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
        Store = new StackStore(scopes, options, TimeProvider.System, new EphemeralDataProtectionProvider());
        Docker = new DockerCli(options, Registries, TimeProvider.System, NullLogger<DockerCli>.Instance);
        Validator = new StackValidator(Compose, Docker, options, NullLogger<StackValidator>.Instance);
        // Tests fetch from local repositories, which the server itself never may.
        Git = new GitCli(options, TimeProvider.System, NullLogger<GitCli>.Instance) { AllowedProtocols = "file" };
        StackGit = new StackGit(Git, NullLogger<StackGit>.Instance);
        Jobs = new StackJobs(Compose, Docker, Store, new Swarm.SwarmServices(Registries, NullLogger<Swarm.SwarmServices>.Instance),
            StackGit, TimeProvider.System, NullLogger<StackJobs>.Instance);
    }

    /// <summary>Skips the test when Compose isn't installed here.</summary>
    public async Task RequireComposeAsync()
    {
        var (tool, problem) = await Compose.StatusAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(tool is null, $"Compose isn't available: {problem}");
    }

    /// <summary>Skips the test when the Docker CLI (for swarm stacks) isn't installed here.</summary>
    public async Task RequireDockerCliAsync()
    {
        var (tool, problem) = await Docker.StatusAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(tool is null, $"The Docker CLI isn't available: {problem}");
    }

    public async ValueTask DisposeAsync()
    {
        Jobs?.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            // Git's object files are read-only, which Windows won't delete otherwise.
            StackStore.DeleteTree(DataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
