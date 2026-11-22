using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sockless.Port.Agents;
using Sockless.Port.Engines;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;
using Sockless.Port.Images;
using Sockless.Port.Registries;
using Sockless.Port.Setup;
using Sockless.Port.Stacks;
using Sockless.Port.Volumes;

namespace Sockless.Port;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers persistence, Data Protection, Identity and first-run setup.</summary>
    public static IServiceCollection AddPortCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PortOptions>()
            .Bind(configuration.GetSection(PortOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.DataPath), "Port:DataPath must be set.")
            .ValidateOnStart();

        services.AddDbContext<PortDbContext>((sp, options) =>
        {
            var port = sp.GetRequiredService<IOptions<PortOptions>>().Value;
            options.UseSqlite($"Data Source={port.DatabasePath}");
        });

        // Data Protection keys encrypt auth cookies and registry credentials, so they must
        // survive container restarts: keep them in the data volume.
        services.AddDataProtection().SetApplicationName("Sockless.Port");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<PortOptions>, ILoggerFactory>((keys, port, loggers) =>
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(port.Value.KeysPath), loggers));

        services.AddIdentityCore<PortUser>(options =>
            {
                // Length over composition rules (NIST SP 800-63B).
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<PortDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddSingleton<SetupState>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EngineConnections>();
        services.AddSingleton<LocalEngineDetector>();
        services.TryAddSingleton<IEngineEventSink, NullEngineEventSink>();
        services.AddSingleton<InternalCa>();
        services.AddSingleton<IAgentEnroller, AgentEnrolments>();
        services.AddSingleton<EndpointRegistry>();
        services.AddHostedService<EngineMonitor>();

        services.AddSingleton<RegistryCredentials>();
        services.AddSingleton<ImageJobs>();
        services.AddSingleton<ImageUpdates>();
        services.AddSingleton<VolumeBrowser>();

        services.AddSingleton<ComposeCli>();
        services.AddSingleton<DockerCli>();
        services.AddSingleton<Swarm.SwarmServices>();
        services.AddSingleton<StackStore>();
        services.AddSingleton<StackValidator>();
        services.AddSingleton<StackJobs>();
        return services;
    }

    /// <summary>Creates the data directory, applies migrations and checks whether setup is needed.</summary>
    public static async Task InitializePortAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var port = services.GetRequiredService<IOptions<PortOptions>>().Value;
        Directory.CreateDirectory(port.DataPath);
        Directory.CreateDirectory(port.KeysPath);
        // Build contexts left by a crash mid-upload or mid-build.
        if (Directory.Exists(port.TempPath))
            Directory.Delete(port.TempPath, recursive: true);
        Directory.CreateDirectory(port.TempPath);
        Directory.CreateDirectory(port.StacksPath);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await services.GetRequiredService<SetupState>().InitializeAsync(db, cancellationToken);
        await RegisterEndpointsAsync(db, port, services, cancellationToken);
    }

    /// <summary>
    /// Makes sure the local endpoint exists (detecting its address on first start), then
    /// registers every stored endpoint so the monitor connects to it.
    /// </summary>
    private static async Task RegisterEndpointsAsync(PortDbContext db, PortOptions port, IServiceProvider services, CancellationToken cancellationToken)
    {
        var local = await db.Endpoints.FirstOrDefaultAsync(e => e.Kind == EndpointKind.Local, cancellationToken);
        if (local is null || !string.IsNullOrWhiteSpace(port.LocalEngine))
        {
            var address = await services.GetRequiredService<LocalEngineDetector>().DetectAsync(port.LocalEngine, cancellationToken);
            if (local is null)
                db.Endpoints.Add(new EngineEndpoint { Name = "local", Address = address.ToString(), Kind = EndpointKind.Local });
            else
                local.Address = address.ToString();
            await db.SaveChangesAsync(cancellationToken);
        }

        await services.GetRequiredService<EndpointRegistry>().RegisterAllAsync(db, cancellationToken);
    }
}
