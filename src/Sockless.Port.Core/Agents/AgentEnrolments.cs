using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Docker.Tunnel;
using Sockless.Port.Engines;

namespace Sockless.Port.Agents;

/// <summary>
/// Enrols agents: the server connects to the agent (directly, or through the tunnel an edge agent
/// dialled), which waits with the token from its install command. The agent proves it holds the
/// token over its certificate request, the server signs the request and proves it holds the token
/// over the certificates. From then on both sides use mutual TLS, and the server pins the agent's certificate.
/// </summary>
public sealed partial class AgentEnrolments(
    IServiceScopeFactory scopes,
    InternalCa ca,
    TimeProvider time,
    ILogger<AgentEnrolments> logger) : IAgentEnroller
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Overridable by tests that serve a fake agent.</summary>
    internal Func<HttpMessageHandler> HandlerFactory { get; init; } = CreateHandler;

    public async Task<EnrolmentResult> EnrolAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Enrolment is not { } pending)
            return new EnrolmentFailed("Nothing to enrol.");

        string host, where;
        Uri baseAddress;
        HttpMessageHandler handler;
        switch (connection.Address)
        {
            case TcpAddress tcp:
                host = tcp.Host;
                where = $"{tcp.Host}:{tcp.Port.ToString(CultureInfo.InvariantCulture)}";
                baseAddress = new Uri($"https://{(tcp.Host.Contains(':', StringComparison.Ordinal) ? $"[{tcp.Host}]" : tcp.Host)}:{tcp.Port.ToString(CultureInfo.InvariantCulture)}/");
                handler = HandlerFactory();
                break;
            case TunnelAddress tunnel:
                host = tunnel.Host;
                where = "the end of the tunnel";
                baseAddress = new Uri($"https://{tunnel.Host}/");
                handler = CreateTunnelHandler(tunnel);
                break;
            default:
                return new EnrolmentFailed("Nothing to enrol.");
        }

        using var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = RequestTimeout,
        };

        try
        {
            // 1. The agent's certificate request, with its proof over our nonce.
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            using var csrResponse = await http.PostAsJsonAsync(AgentProtocol.CsrPath.TrimStart('/'),
                new AgentCsrRequest { TokenId = pending.Token.Id, Nonce = nonce }, AgentJsonContext.Default.AgentCsrRequest, cancellationToken);
            if (await AgentRefusalAsync(csrResponse, where, cancellationToken) is { } refused)
                return refused;
            var csr = await csrResponse.Content.ReadFromJsonAsync(AgentJsonContext.Default.AgentCsrResponse, cancellationToken);
            if (csr is null || !AgentProtocol.ProofMatches(AgentProtocol.CsrProof(pending.Token.Secret, nonce, csr.CsrPem), csr.Proof))
            {
                LogBadProof(logger, connection.Name, where);
                return new EnrolmentFailed($"Something at {where} answered, but couldn't prove it was started with this enrolment's command. Check the address.");
            }

            // 2. Sign it, and remember the certificate before the agent has it: if its answer is
            // lost, the next attempt finds the agent enrolled and connects with this certificate.
            IssuedCertificate issued;
            try
            {
                issued = ca.SignAgent(csr.CsrPem, connection.Id, host);
            }
            catch (CertificateRequestException ex)
            {
                return new EnrolmentFailed(ex.Message);
            }
            if (!await StoreCertificateAsync(connection, issued, cancellationToken))
                return new EnrolmentFailed("The endpoint was removed.");

            // 3. Hand it over.
            var caPem = ca.CaCertificatePem;
            using var completeResponse = await http.PostAsJsonAsync(AgentProtocol.CompletePath.TrimStart('/'), new AgentCompleteRequest
            {
                TokenId = pending.Token.Id,
                CertificatePem = issued.CertificatePem,
                CaCertificatePem = caPem,
                Proof = AgentProtocol.CompleteProof(pending.Token.Secret, issued.CertificatePem, caPem),
            }, AgentJsonContext.Default.AgentCompleteRequest, cancellationToken);
            if (await AgentRefusalAsync(completeResponse, where, cancellationToken) is { } refusedCompletion)
                return refusedCompletion;

            LogEnrolled(logger, connection.Name, where, csr.AgentVersion ?? "unknown");
            return new EnrolmentIssued();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            return new EnrolmentFailed(Describe(ex, where));
        }
    }

    public async Task CompleteAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == connection.Id, cancellationToken) is not { } endpoint)
            return;
        endpoint.EnrolmentTokenId = null;
        endpoint.ProtectedEnrolmentSecret = null;
        endpoint.EnrolmentExpiresAt = null;
        endpoint.EnrolledAt ??= now;
        await db.SaveChangesAsync(cancellationToken);

        connection.SetEnrolment(null);
        if (connection.Agent is { } agent)
            connection.SetAgent(connection.Address, agent with { EnrolledAt = endpoint.EnrolledAt });
    }

    private async Task<bool> StoreCertificateAsync(EngineConnection connection, IssuedCertificate issued, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == connection.Id, cancellationToken) is not { } endpoint)
            return false;
        endpoint.AgentCertificatePem = issued.CertificatePem;
        endpoint.AgentThumbprint = issued.Thumbprint;
        endpoint.AgentCertificateNotAfter = issued.NotAfter;
        endpoint.AgentPreviousThumbprint = null;
        await db.SaveChangesAsync(cancellationToken);

        connection.SetAgent(WithTls(connection.Address, ca.AgentTls(issued.Thumbprint)), new AgentStatus(issued.Thumbprint, issued.NotAfter, null));
        return true;
    }

    /// <summary>The agent's address with other TLS settings: a new certificate's pin.</summary>
    internal static DockerAddress WithTls(DockerAddress address, DockerTls tls) => address switch
    {
        TcpAddress tcp => tcp with { Tls = tls },
        TunnelAddress tunnel => tunnel with { Tls = tls },
        _ => throw new InvalidOperationException($"An agent can't be at {address}."),
    };

    /// <summary>The agent's refusal as a result, or null when it accepted.</summary>
    private static async Task<EnrolmentResult?> AgentRefusalAsync(HttpResponseMessage response, string where, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return null;
        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.AgentError, cancellationToken))?.Message;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Conflict => new EnrolmentAlreadyDone(
                $"The agent at {where} says: {message ?? "it's already enrolled."} If it was enrolled before, remove its container and the sockless-port-agent volume, then run the command again."),
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                new EnrolmentFailed($"Something answers at {where}, but it isn't a Sockless Port agent waiting for enrolment."),
            _ when message is not null => new EnrolmentFailed($"The agent at {where} refused: {message}"),
            _ => new EnrolmentFailed($"The agent at {where} answered {(int)response.StatusCode} {response.ReasonPhrase}."),
        };
    }

    private static string Describe(Exception exception, string where)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TunnelUnavailableException tunnel:
                    return tunnel.Message;
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return $"No agent answers at {where} yet. Run the command on that host; this page updates when the agent is enrolled.";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return $"Can't resolve {where}. Check the address.";
                case SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                    return $"Can't reach {where}. Check that the port is open to Sockless Port (firewall, published port).";
                case AuthenticationException:
                    return $"Something answers at {where}, but not over TLS, so it isn't a Sockless Port agent.";
            }
        }
        return exception is TaskCanceledException
            ? $"Timed out waiting for {where}. Check that the port is open to Sockless Port (firewall, published port)."
            : $"Can't enrol the agent at {where}: {Innermost(exception).Message}";
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null)
            exception = exception.InnerException;
        return exception;
    }

    /// <summary>As <see cref="CreateHandler"/>, through the edge agent's tunnel.</summary>
    private static SocketsHttpHandler CreateTunnelHandler(TunnelAddress tunnel)
    {
        var handler = CreateHandler();
        handler.ConnectCallback = (_, ct) => tunnel.Connector.OpenAsync(ct);
        return handler;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseProxy = false,
        UseCookies = false,
        // The agent's certificate is self-signed until this enrolment completes: the proofs, not
        // TLS, authenticate both sides here. Every later connection checks the issued certificate.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled the agent of endpoint {Endpoint} at {Address} (agent {Version})")]
    private static partial void LogEnrolled(ILogger logger, string endpoint, string address, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent for endpoint {Endpoint} at {Address} sent a wrong enrolment proof")]
    private static partial void LogBadProof(ILogger logger, string endpoint, string address);
}
