using System.Globalization;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Settings;
using Sockless.Port.Stacks;

namespace Sockless.Port.Data;

public sealed class PortDbContext(DbContextOptions<PortDbContext> options) : IdentityDbContext<PortUser>(options)
{
    public DbSet<EngineEndpoint> Endpoints => Set<EngineEndpoint>();

    public DbSet<RegistryCredential> Registries => Set<RegistryCredential>();

    public DbSet<StackDefinition> Stacks => Set<StackDefinition>();

    public DbSet<EndpointGrant> EndpointGrants => Set<EndpointGrant>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PortUser>(user =>
        {
            user.Property(u => u.CreatedAt).HasConversion(SortableUtc);
            user.Property(u => u.LastSignInAt).HasConversion(SortableUtc);
            user.Property(u => u.DefaultRole).HasConversion<string>().HasMaxLength(16);
            user.Property(u => u.TimeDisplay).HasConversion<string>().HasMaxLength(8);
        });

        builder.Entity<EndpointGrant>(grant =>
        {
            grant.ToTable("EndpointGrants");
            grant.HasKey(g => new { g.UserId, g.EndpointId });
            grant.HasOne<PortUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne<EngineEndpoint>().WithMany().HasForeignKey(g => g.EndpointId).OnDelete(DeleteBehavior.Cascade);
            grant.Property(g => g.Role).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("AuditLog");
            entry.Property(e => e.At).HasConversion(SortableUtc);
            entry.HasIndex(e => e.At);
            entry.Property(e => e.UserName).HasMaxLength(256);
            entry.HasIndex(e => e.UserName);
            entry.Property(e => e.EndpointName).HasMaxLength(64);
            entry.Property(e => e.Action).HasMaxLength(128);
            entry.Property(e => e.Target).HasMaxLength(AuditLog.MaxTarget);
            entry.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16);
            entry.Property(e => e.Detail).HasMaxLength(AuditLog.MaxDetail);
            entry.Property(e => e.RemoteAddress).HasMaxLength(64);
            // No foreign keys: entries outlive the users and endpoints they name.
        });

        builder.Entity<SettingEntry>(setting =>
        {
            setting.ToTable("Settings");
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Key).HasMaxLength(64);
            setting.Property(s => s.Value).HasMaxLength(1024);
        });

        builder.Entity<EngineEndpoint>(endpoint =>
        {
            endpoint.ToTable("Endpoints");
            endpoint.Property(e => e.Name).HasMaxLength(64);
            endpoint.HasIndex(e => e.Name).IsUnique();
            endpoint.Property(e => e.Address).HasMaxLength(512);
            endpoint.Property(e => e.CreatedAt).HasConversion(SortableUtc);
            endpoint.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            endpoint.Ignore(e => e.IsLocal);
            endpoint.Property(e => e.AgentThumbprint).HasMaxLength(64);
            endpoint.Property(e => e.AgentCertificateNotAfter).HasConversion(SortableUtc);
            endpoint.Property(e => e.EnrolledAt).HasConversion(SortableUtc);
            endpoint.Property(e => e.EnrolmentTokenId).HasMaxLength(32);
            endpoint.Property(e => e.EnrolmentExpiresAt).HasConversion(SortableUtc);
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
