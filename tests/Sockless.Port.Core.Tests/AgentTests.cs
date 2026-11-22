using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Engines;

namespace Sockless.Port.Core.Tests;

public sealed class AgentTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"pki-{Guid.NewGuid():n}");

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

    private InternalCa CreateCa(IDataProtectionProvider? protection = null) =>
        new(Options.Create(new PortOptions { DataPath = _dataPath }),
            protection ?? DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dataPath, "keys"))),
            TimeProvider.System, NullLogger<InternalCa>.Instance);

    private static string Csr(out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=whatever the agent asks for", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
    }

    // ---------------------------------------------------------------- the CA

    [Theory]
    [InlineData("agent.example.com")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::1")]
    public void Agent_certificates_are_for_the_host_and_server_authentication_only(string host)
    {
        var ca = CreateCa();
        var issued = ca.SignAgent(Csr(out var key), endpointId: 7, host);
        using var _ = key;
        using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem);

        Assert.Equal("CN=sockless-port-agent-7", certificate.Subject);
        Assert.Equal(DockerTransport.Thumbprint(certificate), issued.Thumbprint);
        Assert.True(certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()));
        Assert.True(certificate.MatchesHostname(host));
        Assert.False(certificate.MatchesHostname("other.example.com"));
        var usage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.False(Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);

        using var root = X509Certificate2.CreateFromPem(ca.CaCertificatePem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(certificate));
    }

    [Fact]
    public void The_server_presents_a_client_certificate_from_the_same_ca()
    {
        var ca = CreateCa();
        var tls = ca.AgentTls("ABCDEF");

        Assert.Equal("ABCDEF", tls.PinnedCertificateSha256);
        Assert.Equal(ca.CaCertificatePem, tls.CaCertificatePem);
        using var client = X509Certificate2.CreateFromPem(tls.ClientCertificatePem!, tls.ClientKeyPem!);
        Assert.Equal($"CN={AgentProtocol.ServerCommonName}", client.Subject);
        var usage = Assert.Single(client.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(["1.3.6.1.5.5.7.3.2"], usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
    }

    [Fact]
    public void The_ca_survives_a_restart_and_keeps_its_key_encrypted()
    {
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dataPath, "keys")));
        var first = CreateCa(protection).CaCertificatePem;
        var again = CreateCa(protection).CaCertificatePem;

        Assert.Equal(first, again);
        var keyFile = File.ReadAllText(Path.Combine(_dataPath, "pki", "ca.key.protected"));
        Assert.DoesNotContain("PRIVATE KEY", keyFile);
    }

    [Fact]
    public void Garbage_and_weak_requests_are_refused()
    {
        var ca = CreateCa();
        Assert.Throws<CertificateRequestException>(() => ca.SignAgent("-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----", 1, "host"));

        using var weak = RSA.Create(1024);
        var weakCsr = new CertificateRequest("CN=x", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        Assert.Throws<CertificateRequestException>(() => ca.SignAgent(weakCsr, 1, "host"));
    }

    // ---------------------------------------------------------------- addresses and commands

    [Theory]
    [InlineData("agent.example.com", "agent.example.com", 9001)]
    [InlineData("Agent.Example.com:9443", "agent.example.com", 9443)]
    [InlineData("https://192.0.2.10:9001/", "192.0.2.10", 9001)]
    [InlineData("tcp://192.0.2.10", "192.0.2.10", 9001)]
    [InlineData("[2001:db8::1]:9002", "2001:db8::1", 9002)]
    [InlineData("2001:db8::1", "2001:db8::1", 9001)]
    public void Agent_addresses_are_read_with_a_default_port(string text, string host, int port)
    {
        var errors = new List<EndpointInputError>();
        var address = EndpointRegistry.ParseAgentAddress(text, errors);

        Assert.Empty(errors);
        Assert.Equal(host, address!.Host);
        Assert.Equal(port, address.Port);
        Assert.NotNull(address.Tls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("agent.example.com:0")]
    [InlineData("agent.example.com:99999")]
    [InlineData("agent_1.example.com")]
    [InlineData("agent.example.com/path")]
    [InlineData("http://")]
    public void Bad_agent_addresses_say_what_to_type(string text)
    {
        var errors = new List<EndpointInputError>();
        Assert.Null(EndpointRegistry.ParseAgentAddress(text, errors));
        Assert.Equal("Address", Assert.Single(errors).Field);
    }

    [Theory]
    [InlineData(AgentInstallVariant.Docker, "docker run -d --name sockless-port-agent")]
    [InlineData(AgentInstallVariant.PodmanRootful, "sudo podman run -d")]
    [InlineData(AgentInstallVariant.PodmanRootless, "$XDG_RUNTIME_DIR/podman/podman.sock:/var/run/docker.sock")]
    [InlineData(AgentInstallVariant.Swarm, "docker service create --name sockless-port-agent --mode global")]
    public void Install_commands_carry_the_token_port_and_image(AgentInstallVariant variant, string expected)
    {
        var token = AgentToken.Create();
        var command = AgentCommands.Install(variant, token, 9443, "example/agent:1.2.3");

        Assert.Contains(expected, command.Text);
        Assert.Contains(token.Reveal(), command.Text);
        Assert.Contains("9443", command.Text);
        Assert.EndsWith("example/agent:1.2.3", command.Text);
        // Every line but the last continues.
        var lines = command.Text.Split('\n');
        Assert.All(lines[..^1], l => Assert.EndsWith("\\", l));
    }

    [Fact]
    public void Update_commands_keep_the_identity_and_drop_the_token()
    {
        var command = AgentCommands.Update(AgentInstallVariant.Docker, 9001, "example/agent:2.0.0");
        Assert.StartsWith("docker pull example/agent:2.0.0 && docker rm -f sockless-port-agent", command.Text);
        Assert.Contains("-v sockless-port-agent:/data", command.Text);
        Assert.DoesNotContain("AGENT_TOKEN", command.Text);
    }

    [Theory]
    [InlineData("0.0.1", true)]
    [InlineData("999.0.0", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Older_agents_are_flagged(string? version, bool outdated)
    {
        Assert.Equal(outdated, AgentCommands.IsOutdated(version));
    }

    // ---------------------------------------------------------------- upgrade

    [Fact]
    public async Task The_upgrade_keeps_the_local_endpoint_local()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20261122085740_AddStacks", ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Endpoints (Name, Address, IsLocal, CreatedAt) VALUES ('local', 'unix:///var/run/docker.sock', 1, '2026-11-21T00:00:00.0000000Z')", ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(ct);
        Assert.Equal(EndpointKind.Local, endpoint.Kind);
        Assert.Null(endpoint.AgentThumbprint);
    }
}
