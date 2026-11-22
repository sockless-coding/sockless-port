using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Docker.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void Tokens_round_trip_through_the_install_command()
    {
        var token = AgentToken.Create();
        var text = token.Reveal();

        Assert.StartsWith("spa1.", text);
        Assert.True(AgentToken.TryParse(text, out var parsed));
        Assert.Equal(token.Id, parsed.Id);
        Assert.Equal(token.Secret, parsed.Secret);
        // The secret never shows up by accident.
        Assert.DoesNotContain(text[(text.LastIndexOf('.') + 1)..], token.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("spa1.short.secret")]
    [InlineData("spa2.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("spa1.AAAAAAAAAAAAAAAA.!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void Malformed_tokens_are_refused(string? text)
    {
        Assert.False(AgentToken.TryParse(text, out _));
    }

    [Fact]
    public void Proofs_bind_the_secret_to_each_message()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var proof = AgentProtocol.CsrProof(secret, "nonce", "csr");

        Assert.True(AgentProtocol.ProofMatches(proof, AgentProtocol.CsrProof(secret, "nonce", "csr")));
        Assert.False(AgentProtocol.ProofMatches(proof, AgentProtocol.CsrProof(secret, "other nonce", "csr")));
        Assert.False(AgentProtocol.ProofMatches(proof, AgentProtocol.CsrProof(RandomNumberGenerator.GetBytes(32), "nonce", "csr")));
        // Parts can't be shifted from one to the next, and the two messages can't stand in for each other.
        Assert.False(AgentProtocol.ProofMatches(AgentProtocol.CsrProof(secret, "ab", "c"), AgentProtocol.CsrProof(secret, "a", "bc")));
        Assert.False(AgentProtocol.ProofMatches(proof, AgentProtocol.CompleteProof(secret, "nonce", "csr")));
        Assert.False(AgentProtocol.ProofMatches(proof, null));
    }

    [Fact]
    public void A_pinned_certificate_must_also_be_the_one_pinned()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        X509Certificate2 Issue(string name)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            return request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(8));
        }

        using var pinned = Issue("agent");
        using var other = Issue("agent");
        var pin = DockerTransport.Thumbprint(pinned);

        Assert.True(DockerTransport.ValidateAgainstCustomCa(pinned, SslPolicyErrors.None, ca, pin));
        Assert.False(DockerTransport.ValidateAgainstCustomCa(other, SslPolicyErrors.None, ca, pin));
        Assert.True(DockerTransport.ValidateAgainstCustomCa(other, SslPolicyErrors.None, ca));
        Assert.False(DockerTransport.ValidateAgainstCustomCa(pinned, SslPolicyErrors.RemoteCertificateNameMismatch, ca, pin));
    }
}
