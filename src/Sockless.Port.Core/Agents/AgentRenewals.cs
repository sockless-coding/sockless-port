using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Engines;

namespace Sockless.Port.Agents;

/// <summary>
/// Renews agents' certificates before they expire, over the mutual TLS the current one gives:
/// the agent makes a new key and a request for it, the CA signs it, and the agent serves the new
/// certificate. Until the agent is known to serve it, the server accepts both, so a renewal whose
/// answer is lost can't lock the agent out. Checked hourly for online agents; administrators can
/// also renew at once from the endpoint's settings.
/// </summary>
public sealed partial class AgentRenewals(
    EngineConnections connections,
    IServiceScopeFactory scopes,
    InternalCa ca,
    TimeProvider time,
    ILogger<AgentRenewals> logger,
    Notifications.Alerts? alerts = null) : BackgroundService
{
    /// <summary>Certificates are renewed this long before they expire (they last five years).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(60);

    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAllAsync(stoppingToken);
                await Task.Delay(CheckInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Renews what's due and forgets previous pins the agents no longer serve.</summary>
    internal async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in connections.All)
        {
            if (connection is not { IsAgent: true, Agent: { Thumbprint: not null } agent } || connection.Client is null)
                continue;

            if (agent.Info?.CertificateSha256 is { } served && string.Equals(served, agent.Thumbprint, StringComparison.OrdinalIgnoreCase))
                await ForgetPreviousAsync(connection, cancellationToken);

            var notAfter = agent.Info?.CertificateNotAfter ?? agent.CertificateNotAfter;
            if (notAfter is not { } expires || expires - time.GetUtcNow() > RenewBefore)
                continue;

            if (await RenewAsync(connection.Id, cancellationToken) is { } error)
            {
                LogFailed(logger, connection.Name, error);
                if (alerts is not null)
                    await alerts.TaskFailedAsync(connection, $"Couldn't renew the agent's certificate on {connection.Name}", error, null, cancellationToken);
            }
        }
    }

    /// <summary>Renews the endpoint's agent certificate now. Null on success, otherwise what went wrong.</summary>
    public async Task<string?> RenewAsync(int endpointId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RenewCoreAsync(endpointId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RenewCoreAsync(int endpointId, CancellationToken cancellationToken)
    {
        if (connections.Get(endpointId) is not { IsAgent: true, Agent: { Thumbprint: { } current } agent } connection)
            return "Only an enrolled agent's certificate can be renewed.";
        if (connection.Client is not { } client)
            return "The agent isn't connected. Its certificate can be renewed once it's online.";

        var host = connection.Address switch
        {
            TcpAddress tcp => tcp.Host,
            TunnelAddress tunnel => tunnel.Host,
            _ => null,
        };
        if (host is null)
            return "Only an agent's certificate can be renewed.";

        // 1. A request for a fresh key; the agent keeps serving its current certificate meanwhile.
        AgentRenewCsrResponse csr;
        try
        {
            csr = await client.RequestAgentRenewalAsync(cancellationToken);
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return "This agent is too old to renew its certificate. Update it (the command is on the endpoint's settings page), or enrol it again.";
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return $"The agent didn't make a new key: {EngineErrors.ForCall(ex, connection.Address)}";
        }

        IssuedCertificate issued;
        try
        {
            issued = ca.SignAgent(csr.CsrPem, endpointId, host);
        }
        catch (CertificateRequestException ex)
        {
            return ex.Message;
        }

        // 2. Store it, still accepting what the agent serves now.
        var served = agent.Info?.CertificateSha256 ?? current;
        if (!await StoreAsync(endpointId, issued, previous: served, cancellationToken))
            return "The endpoint was removed.";
        connection.SetAgent(AgentEnrolments.WithTls(connection.Address, ca.AgentTls(issued.Thumbprint, served)),
            agent with { Thumbprint = issued.Thumbprint, CertificateNotAfter = issued.NotAfter });

        // 3. Hand it over. Whatever happens, reconnect: the live client only knows the old pin.
        try
        {
            var info = await client.CompleteAgentRenewalAsync(new AgentRenewRequest
            {
                CertificatePem = issued.CertificatePem,
                CaCertificatePem = ca.CaCertificatePem,
            }, cancellationToken);

            await StoreAsync(endpointId, issued, previous: null, cancellationToken);
            connection.SetAgent(AgentEnrolments.WithTls(connection.Address, ca.AgentTls(issued.Thumbprint)),
                agent with { Thumbprint = issued.Thumbprint, CertificateNotAfter = issued.NotAfter, Info = info });
            LogRenewed(logger, connection.Name, issued.NotAfter);
            return null;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return $"The agent didn't take its new certificate: {EngineErrors.ForCall(ex, connection.Address)} It still works with its current one; the renewal is tried again within the hour.";
        }
        finally
        {
            if (connections.Get(endpointId) == connection)
                connections.Reconnect(connection);
        }
    }

    private async Task<bool> StoreAsync(int endpointId, IssuedCertificate issued, string? previous, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == endpointId, cancellationToken) is not { } endpoint)
            return false;
        endpoint.AgentCertificatePem = issued.CertificatePem;
        endpoint.AgentThumbprint = issued.Thumbprint;
        endpoint.AgentCertificateNotAfter = issued.NotAfter;
        endpoint.AgentPreviousThumbprint = previous == issued.Thumbprint ? null : previous;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The agent serves the current certificate: stop accepting the one before it.</summary>
    private async Task ForgetPreviousAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == connection.Id, cancellationToken) is not { AgentPreviousThumbprint: not null, AgentThumbprint: { } pin } endpoint)
            return;
        endpoint.AgentPreviousThumbprint = null;
        await db.SaveChangesAsync(cancellationToken);
        if (connection.Agent is { } agent)
            connection.SetAgent(AgentEnrolments.WithTls(connection.Address, ca.AgentTls(pin)), agent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Renewed the agent certificate of endpoint {Endpoint}, valid until {NotAfter}")]
    private static partial void LogRenewed(ILogger logger, string endpoint, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't renew the agent certificate of endpoint {Endpoint}: {Error}")]
    private static partial void LogFailed(ILogger logger, string endpoint, string error);
}
