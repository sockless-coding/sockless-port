using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Agent;

/// <summary>
/// The agent's own HTTP API, next to the relayed Engine API. Enrolment runs before the agent has
/// a certificate, so both sides prove they hold the enrolment token's secret with an HMAC over
/// each message instead of sending it: a captured exchange reveals nothing that enrols anything else.
/// </summary>
public static class AgentProtocol
{
    /// <summary>The port the agent listens on unless told otherwise.</summary>
    public const int DefaultPort = 9001;

    public const string CsrPath = "/agent/enrol/csr";
    public const string CompletePath = "/agent/enrol/complete";
    public const string InfoPath = "/agent/info";

    /// <summary>Answers without a client certificate, for the container's health check. Says nothing about the host.</summary>
    public const string HealthPath = "/agent/healthz";

    /// <summary>
    /// Common name of the server's client certificate. Agents accept only clients whose certificate
    /// chains to the CA from enrolment, allows client authentication and carries this name.
    /// </summary>
    public const string ServerCommonName = "sockless-port-server";

    /// <summary>Common name of an agent's server certificate.</summary>
    public static string AgentCommonName(int endpointId) => $"sockless-port-agent-{endpointId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private const string CsrLabel = "sockless-port/agent-csr/v1";
    private const string CompleteLabel = "sockless-port/agent-complete/v1";

    /// <summary>The agent's proof over its CSR, bound to the server's nonce.</summary>
    public static string CsrProof(ReadOnlySpan<byte> secret, string nonce, string csrPem) => Proof(secret, CsrLabel, nonce, csrPem);

    /// <summary>The server's proof over the certificates it issued.</summary>
    public static string CompleteProof(ReadOnlySpan<byte> secret, string certificatePem, string caCertificatePem) =>
        Proof(secret, CompleteLabel, certificatePem, caCertificatePem);

    /// <summary>Compares proofs in constant time.</summary>
    public static bool ProofMatches(string expected, string? actual)
    {
        if (actual is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    /// <summary>HMAC-SHA256 over the label and each part, every one length-prefixed so parts can't run into each other.</summary>
    private static string Proof(ReadOnlySpan<byte> secret, string label, params ReadOnlySpan<string> parts)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        Append(hmac, label);
        foreach (var part in parts)
            Append(hmac, part);
        return Convert.ToBase64String(hmac.GetHashAndReset());

        static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}

/// <summary>
/// An enrolment token, as it appears in the install command: <c>spa1.{id}.{secret}</c>. The id
/// says which enrolment it is; the secret never travels again after the command is run.
/// </summary>
public sealed class AgentToken
{
    private const string Prefix = "spa1";

    private AgentToken(string id, byte[] secret)
    {
        Id = id;
        Secret = secret;
    }

    public string Id { get; }

    public byte[] Secret { get; }

    public static AgentToken Create() =>
        new(Base64Url(RandomNumberGenerator.GetBytes(12)), RandomNumberGenerator.GetBytes(32));

    public static AgentToken FromParts(string id, byte[] secret) => new(id, secret);

    public static bool TryParse(string? value, [NotNullWhen(true)] out AgentToken? token)
    {
        token = null;
        var parts = value?.Trim().Split('.');
        if (parts is not [Prefix, { Length: 16 } id, { Length: 43 } secret])
            return false;
        try
        {
            var bytes = Convert.FromBase64String(FromBase64Url(secret));
            if (bytes.Length != 32)
                return false;
            token = new AgentToken(id, bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The token as typed into the command. It's a secret: don't log it.</summary>
    public string Reveal() => $"{Prefix}.{Id}.{Base64Url(Secret)}";

    public override string ToString() => $"AgentToken({Id})";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return padded + new string('=', (4 - padded.Length % 4) % 4);
    }
}

/// <summary>Server → agent: start enrolment.</summary>
public sealed record AgentCsrRequest
{
    public required string TokenId { get; init; }

    /// <summary>Fresh per attempt, so an old answer can't be replayed.</summary>
    public required string Nonce { get; init; }
}

/// <summary>Agent → server: a certificate request for a key that never leaves the agent.</summary>
public sealed record AgentCsrResponse
{
    public required string CsrPem { get; init; }

    public required string Proof { get; init; }

    public string? AgentVersion { get; init; }
}

/// <summary>Server → agent: the issued certificate and the CA its clients chain to.</summary>
public sealed record AgentCompleteRequest
{
    public required string TokenId { get; init; }

    public required string CertificatePem { get; init; }

    public required string CaCertificatePem { get; init; }

    public required string Proof { get; init; }
}

/// <summary>What the agent reports about itself (<see cref="AgentProtocol.InfoPath"/>).</summary>
public sealed record AgentInfo
{
    public string? Version { get; init; }

    /// <summary>The engine address the agent relays to, e.g. <c>unix:///var/run/docker.sock</c>.</summary>
    public string? Engine { get; init; }

    public string? Os { get; init; }

    public string? Architecture { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EnrolledAt { get; init; }

    public DateTimeOffset? CertificateNotAfter { get; init; }
}

/// <summary>Errors from the agent's own endpoints, shaped like the engine's so clients read both the same way.</summary>
public sealed record AgentError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentCsrRequest))]
[JsonSerializable(typeof(AgentCsrResponse))]
[JsonSerializable(typeof(AgentCompleteRequest))]
[JsonSerializable(typeof(AgentInfo))]
[JsonSerializable(typeof(AgentError))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
