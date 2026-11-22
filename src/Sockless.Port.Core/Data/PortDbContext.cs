using System.Globalization;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Stacks;

namespace Sockless.Port.Data;

public sealed class PortDbContext(DbContextOptions<PortDbContext> options) : IdentityDbContext<PortUser>(options)
{
    public DbSet<EngineEndpoint> Endpoints => Set<EngineEndpoint>();

    public DbSet<RegistryCredential> Registries => Set<RegistryCredential>();

    public DbSet<StackDefinition> Stacks => Set<StackDefinition>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PortUser>().Property(u => u.CreatedAt).HasConversion(SortableUtc);

        builder.Entity<EngineEndpoint>(endpoint =>
        {
            endpoint.ToTable("Endpoints");
            endpoint.Property(e => e.Name).HasMaxLength(64);
            endpoint.HasIndex(e => e.Name).IsUnique();
            endpoint.Property(e => e.Address).HasMaxLength(512);
            endpoint.Property(e => e.CreatedAt).HasConversion(SortableUtc);
        });

        builder.Entity<RegistryCredential>(registry =>
        {
            registry.ToTable("Registries");
            registry.Property(r => r.Name).HasMaxLength(64);
            registry.Property(r => r.Registry).HasMaxLength(255);
            // One credential per registry: pulls pick it by the image's registry host.
            registry.HasIndex(r => r.Registry).IsUnique();
            registry.Property(r => r.Username).HasMaxLength(255);
            registry.Property(r => r.CreatedAt).HasConversion(SortableUtc);
            registry.Property(r => r.UpdatedAt).HasConversion(SortableUtc);
        });

        builder.Entity<StackDefinition>(stack =>
        {
            stack.ToTable("Stacks");
            stack.Property(s => s.Name).HasMaxLength(StackNames.MaxLength);
            // Compose identifies a project by its name on an engine.
            stack.HasIndex(s => new { s.EndpointId, s.Name }).IsUnique();
            stack.HasOne<EngineEndpoint>().WithMany().HasForeignKey(s => s.EndpointId).OnDelete(DeleteBehavior.Cascade);
            stack.Property(s => s.Kind).HasConversion<string>().HasMaxLength(16);
            stack.Property(s => s.CreatedAt).HasConversion(SortableUtc);
            stack.Property(s => s.UpdatedAt).HasConversion(SortableUtc);
            stack.Property(s => s.DeployedAt).HasConversion(SortableUtc);
        });
    }

    // SQLite can't order or compare DateTimeOffset natively; store it as a sortable UTC string.
    private static readonly ValueConverter<DateTimeOffset, string> SortableUtc = new(
        v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
