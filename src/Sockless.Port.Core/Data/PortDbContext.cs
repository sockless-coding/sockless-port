using System.Globalization;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;
using Sockless.Port.Registries;
using Sockless.Port.Settings;
using Sockless.Port.Stacks;
using Sockless.Port.Templates;
using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Data;

public sealed class PortDbContext(DbContextOptions<PortDbContext> options) : IdentityDbContext<PortUser>(options)
{
    public DbSet<EngineEndpoint> Endpoints => Set<EngineEndpoint>();

    public DbSet<RegistryCredential> Registries => Set<RegistryCredential>();

    public DbSet<StackDefinition> Stacks => Set<StackDefinition>();

    public DbSet<EndpointGrant> EndpointGrants => Set<EndpointGrant>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<ImageScan> ImageScans => Set<ImageScan>();

    public DbSet<ImageFinding> ImageFindings => Set<ImageFinding>();

    public DbSet<AcceptedRisk> AcceptedRisks => Set<AcceptedRisk>();

    public DbSet<AcceptedRiskComment> AcceptedRiskComments => Set<AcceptedRiskComment>();

    public DbSet<ResourceUsageEntry> ResourceUsage => Set<ResourceUsageEntry>();

    public DbSet<ResourceTotalEntry> ResourceTotals => Set<ResourceTotalEntry>();

    public DbSet<TemplateSource> TemplateSources => Set<TemplateSource>();

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

        builder.Entity<ImageScan>(scan =>
        {
            scan.ToTable("ImageScans");
            // No endpoint: an image ID names the same content on every engine.
            scan.Property(s => s.ImageId).HasMaxLength(80);
            scan.HasIndex(s => s.ImageId).IsUnique();
            scan.Property(s => s.ImageName).HasMaxLength(512);
            scan.Property(s => s.ScannedAt).HasConversion(SortableUtc);
            scan.Property(s => s.ScannerVersion).HasMaxLength(32);
            scan.Property(s => s.DatabaseUpdatedAt).HasConversion(SortableUtc);
            scan.Property(s => s.Os).HasMaxLength(128);
            scan.Ignore(s => s.Counts);
            scan.HasMany(s => s.Findings).WithOne().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ImageFinding>(finding =>
        {
            finding.ToTable("ImageFindings");
            finding.Property(f => f.VulnerabilityId).HasMaxLength(64);
            finding.Property(f => f.Package).HasMaxLength(256);
            finding.Property(f => f.InstalledVersion).HasMaxLength(256);
            finding.Property(f => f.FixedVersion).HasMaxLength(512);
            finding.Property(f => f.Severity).HasConversion<string>().HasMaxLength(16);
            finding.Property(f => f.Status).HasMaxLength(32);
            finding.Property(f => f.Title).HasMaxLength(512);
            finding.Property(f => f.Target).HasMaxLength(1024);
            finding.Property(f => f.Type).HasMaxLength(64);
            finding.Property(f => f.Url).HasMaxLength(1024);
            // Accepted risks are matched by vulnerability.
            finding.HasIndex(f => new { f.ScanId, f.VulnerabilityId });
        });

        builder.Entity<AcceptedRisk>(risk =>
        {
            risk.ToTable("AcceptedRisks");
            risk.Property(r => r.VulnerabilityId).HasMaxLength(64);
            risk.Property(r => r.Package).HasMaxLength(256);
            risk.Property(r => r.Target).HasMaxLength(1024);
            // No foreign key: an acceptance outlives the scan it came from, and may hold for every image.
            risk.Property(r => r.ImageId).HasMaxLength(80);
            risk.Property(r => r.ImageName).HasMaxLength(512);
            risk.Property(r => r.CreatedBy).HasMaxLength(256);
            risk.Property(r => r.CreatedAt).HasConversion(SortableUtc);
            risk.Property(r => r.ExpiresAt).HasConversion(SortableUtc);
            risk.HasIndex(r => r.VulnerabilityId);
            risk.Ignore(r => r.Key);
            risk.HasMany(r => r.Comments).WithOne().HasForeignKey(c => c.RiskId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AcceptedRiskComment>(comment =>
        {
            comment.ToTable("AcceptedRiskComments");
            comment.Property(c => c.UserName).HasMaxLength(256);
            comment.Property(c => c.At).HasConversion(SortableUtc);
            comment.Property(c => c.Text).HasMaxLength(Vulnerabilities.AcceptedRisks.MaxComment);
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
            stack.Property(s => s.Template).HasMaxLength(StackDefinition.MaxTemplate);
        });

        builder.Entity<TemplateSource>(source =>
        {
            source.ToTable("TemplateSources");
            source.Property(s => s.Name).HasMaxLength(TemplateSource.MaxName);
            source.HasIndex(s => s.Name).IsUnique();
            source.Property(s => s.Url).HasMaxLength(TemplateSource.MaxUrl);
            source.Property(s => s.CreatedAt).HasConversion(SortableUtc);
            source.Property(s => s.FetchedAt).HasConversion(SortableUtc);
            source.Property(s => s.AttemptedAt).HasConversion(SortableUtc);
            source.Property(s => s.Error).HasMaxLength(512);
            source.Property(s => s.Format).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<ResourceUsageEntry>(usage =>
        {
            usage.ToTable("ResourceUsage");
            usage.Property(u => u.Name).HasMaxLength(256);
            usage.Property(u => u.Stack).HasMaxLength(256);
            // Trends read a period of one resolution, for the endpoint or one container in it.
            usage.HasIndex(u => new { u.EndpointId, u.Seconds, u.Time });
            usage.HasIndex(u => new { u.EndpointId, u.Seconds, u.Name, u.Time });
            usage.HasOne<EngineEndpoint>().WithMany().HasForeignKey(u => u.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResourceTotalEntry>(total =>
        {
            total.ToTable("ResourceTotals");
            total.HasIndex(t => new { t.EndpointId, t.Seconds, t.Time });
            total.HasOne<EngineEndpoint>().WithMany().HasForeignKey(t => t.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    // SQLite can't order or compare DateTimeOffset natively; store it as a sortable UTC string.
    private static readonly ValueConverter<DateTimeOffset, string> SortableUtc = new(
        v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
