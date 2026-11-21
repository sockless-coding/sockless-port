using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;
using Sockless.Port.Setup;

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

        // Data Protection keys encrypt auth cookies (and, later, stored secrets), so they must
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
        return services;
    }

    /// <summary>Creates the data directory, applies migrations and checks whether setup is needed.</summary>
    public static async Task InitializePortAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var port = services.GetRequiredService<IOptions<PortOptions>>().Value;
        Directory.CreateDirectory(port.DataPath);
        Directory.CreateDirectory(port.KeysPath);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await services.GetRequiredService<SetupState>().InitializeAsync(db, cancellationToken);
    }
}
