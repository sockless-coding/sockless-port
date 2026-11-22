using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Agents;
using Sockless.Port.Backup;
using Sockless.Port.Https;
using Sockless.Port.Settings;

namespace Sockless.Port.Core.Tests;

/// <summary>Roles, settings, backup paths and the UI's HTTPS certificate.</summary>
public sealed class HardeningTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"hardening-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---------------------------------------------------------------- roles

    [Theory]
    [InlineData(EndpointRole.None, false, false, false)]
    [InlineData(EndpointRole.Viewer, true, false, false)]
    [InlineData(EndpointRole.Operator, true, true, false)]
    [InlineData(EndpointRole.SwarmOperator, true, true, true)]
    public void Each_role_includes_the_ones_before_it(EndpointRole role, bool view, bool operate, bool swarm)
    {
        Assert.Equal(view, role.Allows(EndpointPermission.View));
        Assert.Equal(operate, role.Allows(EndpointPermission.Operate));
        Assert.Equal(swarm, role.Allows(EndpointPermission.ManageSwarm));
    }

    [Fact]
    public void A_grant_overrides_the_default_role_for_its_endpoint_only()
    {
        var access = new UserAccess("u", "ann", IsAdmin: false, EndpointRole.Viewer, new Dictionary<int, EndpointRole>
        {
            [2] = EndpointRole.Operator,
            [3] = EndpointRole.None,
        });

        Assert.Equal(EndpointRole.Viewer, access.RoleFor(1));
        Assert.Equal(EndpointRole.Operator, access.RoleFor(2));
        Assert.False(access.CanView(3));
        // Endpoints added later get the default.
        Assert.True(access.CanView(99));
        Assert.False(access.Can(99, EndpointPermission.Operate));
    }

    [Fact]
    public void Administrators_have_every_role_everywhere()
    {
        var access = new UserAccess("u", "root", IsAdmin: true, EndpointRole.None, new Dictionary<int, EndpointRole> { [1] = EndpointRole.None });

        Assert.True(access.Can(1, EndpointPermission.ManageSwarm));
        Assert.True(access.Can(42, EndpointPermission.ManageSwarm));
    }

    [Theory]
    [InlineData("ann", null)]
    [InlineData("ann.b@example-1", null)]
    [InlineData("a", "Use 2 to 64 characters.")]
    [InlineData("ann b", "Use letters, digits and . _ @ - only.")]
    [InlineData("ann/../b", "Use letters, digits and . _ @ - only.")]
    public void User_names_are_checked(string name, string? problem) => Assert.Equal(problem, UserAdmin.NameProblem(name));

    // ---------------------------------------------------------------- settings

    [Fact]
    public void Settings_out_of_range_are_refused()
    {
        Assert.Empty(AppSettings.Default.Problems());
        Assert.Single((AppSettings.Default with { SessionTimeoutMinutes = 1 }).Problems());
        Assert.Single((AppSettings.Default with { AuditRetentionDays = 1 }).Problems());
        Assert.Equal(2, (AppSettings.Default with { SessionTimeoutMinutes = 0, AuditRetentionDays = 100_000 }).Problems().Count);
    }

    // ---------------------------------------------------------------- backup

    [Theory]
    [InlineData("port.db", true)]
    [InlineData("keys/key-1.xml", true)]
    [InlineData("pki/ca.pem", true)]
    [InlineData("stacks/1/web/compose.yaml", true)]
    [InlineData("stacks/1/web/.deployed/compose.yaml", true)]
    [InlineData("port.db-wal", false)]
    [InlineData("tmp/context.tar", false)]
    [InlineData("stacks/../port.db", false)]
    [InlineData("stacks//x", false)]
    [InlineData("keys", false)]
    [InlineData("other/file", false)]
    public void A_restore_takes_only_what_a_backup_has(string name, bool allowed) => Assert.Equal(allowed, DataBackup.IsAllowed(name));

    [Fact]
    public void Applying_a_pending_restore_keeps_what_it_replaces()
    {
        var port = new PortOptions { DataPath = _dataPath };
        Directory.CreateDirectory(Path.Combine(_dataPath, "keys"));
        File.WriteAllText(port.DatabasePath, "old database");
        File.WriteAllText(Path.Combine(_dataPath, "keys", "old.xml"), "old key");
        Directory.CreateDirectory(Path.Combine(port.RestorePendingPath, "keys"));
        File.WriteAllText(Path.Combine(port.RestorePendingPath, "port.db"), "new database");
        File.WriteAllText(Path.Combine(port.RestorePendingPath, "keys", "new.xml"), "new key");

        var aside = DataBackup.ApplyPending(port, new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(Path.Combine(_dataPath, "before-restore-20261122-100000"), aside);
        Assert.Equal("new database", File.ReadAllText(port.DatabasePath));
        Assert.True(File.Exists(Path.Combine(_dataPath, "keys", "new.xml")));
        Assert.False(File.Exists(Path.Combine(_dataPath, "keys", "old.xml")));
        Assert.Equal("old database", File.ReadAllText(Path.Combine(aside!, "port.db")));
        Assert.True(File.Exists(Path.Combine(aside!, "keys", "old.xml")));
        Assert.False(Directory.Exists(port.RestorePendingPath));
        Assert.Null(DataBackup.ApplyPending(port, DateTimeOffset.UtcNow));
    }

    // ---------------------------------------------------------------- HTTPS

    private InternalCa CreateCa() =>
        new(Options.Create(new PortOptions { DataPath = _dataPath }),
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dataPath, "keys"))),
            TimeProvider.System, NullLogger<InternalCa>.Instance);

    [Fact]
    public void The_https_certificate_names_the_hosts_and_serves_only()
    {
        var ca = CreateCa();

        var certificate = ca.HttpsCertificate(["port.example.com", "192.0.2.5", "localhost"]);

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("CN=sockless-port-ui", certificate.Subject);
        using var caCertificate = X509Certificate2.CreateFromPem(ca.CaCertificatePem);
        Assert.Equal(caCertificate.Subject, certificate.Issuer);
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["localhost", "port.example.com"], san.EnumerateDnsNames().Order());
        Assert.Equal("192.0.2.5", san.EnumerateIPAddresses().Single().ToString());
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(o => o.Value));
        Assert.True(certificate.NotAfter - DateTime.UtcNow <= TimeSpan.FromDays(398));
    }

    [Fact]
    public void The_https_certificate_is_kept_until_the_names_change()
    {
        var first = CreateCa().HttpsCertificate(["a.example.com", "b.example.com"]);

        // A new instance (a restart) loads the stored one; the order of the names doesn't matter.
        var again = CreateCa().HttpsCertificate(["B.example.com", "a.example.com"]);
        var other = CreateCa().HttpsCertificate(["c.example.com"]);

        Assert.Equal(first.Thumbprint, again.Thumbprint);
        Assert.NotEqual(first.Thumbprint, other.Thumbprint);
    }

    [Fact]
    public void A_configured_certificate_file_is_served_and_its_problems_reported()
    {
        Directory.CreateDirectory(_dataPath);
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=files.example.com", ecdsa, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("files.example.com");
        request.CertificateExtensions.Add(names.Build());
        using var source = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        var pem = source.ExportCertificatePem();
        var key = ecdsa.ExportPkcs8PrivateKeyPem();
        var certificatePath = Path.Combine(_dataPath, "tls.crt");
        var keyPath = Path.Combine(_dataPath, "tls.key");
        File.WriteAllText(certificatePath, pem);
        File.WriteAllText(keyPath, key);

        var options = new PortOptions { DataPath = _dataPath, Https = new HttpsOptions { Port = 8443, Certificate = certificatePath, CertificateKey = keyPath } };
        var https = new HttpsCertificates(Options.Create(options), CreateCa(), TimeProvider.System, NullLogger<HttpsCertificates>.Instance);
        var status = https.Status();

        Assert.Null(status.Problem);
        Assert.Equal(source.Thumbprint, https.Current().Thumbprint);
        Assert.Contains("files.example.com", status.Names);
        Assert.False(status.InternalCa);

        var missing = options.Https.Certificate = Path.Combine(_dataPath, "missing.crt");
        var broken = new HttpsCertificates(Options.Create(options), CreateCa(), TimeProvider.System, NullLogger<HttpsCertificates>.Instance).Status();
        Assert.Contains("missing.crt", broken.Problem);
        Assert.True(missing.Length > 0);
    }
}
