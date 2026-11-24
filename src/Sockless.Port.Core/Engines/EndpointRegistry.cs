using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Engines;

/// <summary>A field-level problem with an endpoint form.</summary>
public sealed record EndpointInputError(string Field, string Message);

/// <summary>What a user typed to add an agent endpoint.</summary>
/// <param name="Address">The agent's host, optionally with a port: <c>host</c>, <c>host:9001</c>, <c>[2001:db8::1]:9001</c>.</param>
public sealed record AgentEndpointInput(string Name, string Address);

/// <summary>
/// Adds, renames, re-enrols and removes endpoints, keeping the stored rows and the live
/// connections in step. The local endpoint is found at start and can only be renamed.
/// </summary>
public sealed partial class EndpointRegistry(
    IServiceScopeFactory scopes,
    EngineConnections connections,
    InternalCa ca,
    IDataProtectionProvider protection,
    IOptions<PortOptions> options,
    TimeProvider time,
    ILogger<EndpointRegistry> logger)
{
    /// <summary>How long an install command can enrol its agent.</summary>
    public static readonly TimeSpan EnrolmentLifetime = TimeSpan.FromHours(1);

    public const int MaxNameLength = 64;

    private readonly IDataProtector _protector = protection.CreateProtector("Sockless.Port.AgentEnrolment.v1");

    // ---------------------------------------------------------------- start

    /// <summary>Registers every stored endpoint with the monitor, with its agent certificate and pending enrolment.</summary>
    internal async Task RegisterAllAsync(PortDbContext db, CancellationToken cancellationToken)
    {
        foreach (var endpoint in await db.Endpoints.AsNoTracking().OrderBy(e => e.Id).ToListAsync(cancellationToken))
            connections.Add(ToConnection(endpoint));
    }

    private EngineConnection ToConnection(EngineEndpoint endpoint)
    {
        if (!DockerAddress.TryParse(endpoint.Address, out var address))
            throw new InvalidDataException($"Endpoint '{endpoint.Name}' has an invalid address '{endpoint.Address}'.");
        var connection = new EngineConnection(endpoint.Id, endpoint.Name, address, endpoint.Kind, time);
        if (endpoint.Kind == EndpointKind.Agent && address is TcpAddress tcp)
        {
            var tls = endpoint.AgentThumbprint is { } pin ? ca.AgentTls(pin) : new DockerTls();
            connection.SetAgent(new TcpAddress(tcp.Host, tcp.Port, tls),
                new AgentStatus(endpoint.AgentThumbprint, endpoint.AgentCertificateNotAfter, endpoint.EnrolledAt));
            connection.SetEnrolment(ReadEnrolment(endpoint));
            if (endpoint.AgentThumbprint is null && connection.Enrolment is { } pending)
                connection.SetEnrolling(time.GetUtcNow(), pending.ExpiresAt, problem: null);
        }
        return connection;
    }

    private PendingEnrolment? ReadEnrolment(EngineEndpoint endpoint)
    {
        if (endpoint is not { EnrolmentTokenId: { } id, ProtectedEnrolmentSecret: { } secret, EnrolmentExpiresAt: { } expires })
            return null;
        try
        {
            return new PendingEnrolment(AgentToken.FromParts(id, _protector.Unprotect(Convert.FromBase64String(secret))), expires);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            LogUnreadableEnrolment(logger, ex, endpoint.Name);
            return null;
        }
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Adds an agent endpoint with a fresh enrolment. Returns its id, or the problems (nothing is saved then).</summary>
    public async Task<(int? Id, IReadOnlyList<EndpointInputError> Errors)> AddAgentAsync(AgentEndpointInput input, CancellationToken cancellationToken = default)
    {
        var errors = new List<EndpointInputError>();
        var name = input.Name.Trim();
        CheckName(name, errors);
        var address = ParseAgentAddress(input.Address, errors);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        await CheckNameIsFreeAsync(db, name, null, errors, cancellationToken);
        if (errors.Count > 0)
            return (null, errors);

        var token = AgentToken.Create();
        var endpoint = new EngineEndpoint
        {
            Name = name,
            Address = address!.ToString(),
            Kind = EndpointKind.Agent,
            CreatedAt = time.GetUtcNow(),
        };
        SetEnrolment(endpoint, token);
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        connections.Add(ToConnection(endpoint));
        LogAdded(logger, name, endpoint.Address);
        return (endpoint.Id, []);
    }

    public async Task<IReadOnlyList<EndpointInputError>> RenameAsync(int id, string name, CancellationToken cancellationToken = default)
    {
        var errors = new List<EndpointInputError>();
        name = name.Trim();
        CheckName(name, errors);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                       ?? throw new KeyNotFoundException($"Endpoint {id} doesn't exist.");
        await CheckNameIsFreeAsync(db, name, id, errors, cancellationToken);
        if (errors.Count > 0)
            return errors;

        endpoint.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        if (connections.Get(id) is { } connection)
        {
            connection.SetName(name);
            connections.Touch(connection);
        }
        return [];
    }

    /// <summary>
    /// Starts a new enrolment for an agent endpoint, optionally at a new address. The current
    /// certificate stops being accepted at once, so only an agent started with the new command
    /// gets in.
    /// </summary>
    public async Task<IReadOnlyList<EndpointInputError>> ReenrolAsync(int id, string? newAddress, CancellationToken cancellationToken = default)
    {
        var errors = new List<EndpointInputError>();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                       ?? throw new KeyNotFoundException($"Endpoint {id} doesn't exist.");
        if (endpoint.Kind != EndpointKind.Agent)
            throw new InvalidOperationException("Only agent endpoints are enrolled.");

        if (!string.IsNullOrWhiteSpace(newAddress))
        {
            if (ParseAgentAddress(newAddress, errors) is { } address)
                endpoint.Address = address.ToString();
            if (errors.Count > 0)
                return errors;
        }

        endpoint.AgentCertificatePem = null;
        endpoint.AgentThumbprint = null;
        endpoint.AgentCertificateNotAfter = null;
        endpoint.EnrolledAt = null;
        SetEnrolment(endpoint, AgentToken.Create());
        await db.SaveChangesAsync(cancellationToken);

        if (connections.Get(id) is { } connection)
        {
            var fresh = ToConnection(endpoint);
            connection.SetAgent(fresh.Address, fresh.Agent!);
            connection.SetEnrolment(fresh.Enrolment);
            connections.Reconnect(connection);
        }
        LogReenrolling(logger, endpoint.Name, endpoint.Address);
        return [];
    }

    /// <summary>Removes an agent endpoint, its stacks (records and files) and its connection.</summary>
    /// <returns>The removed endpoint's name, or null if it didn't exist.</returns>
    public async Task<string?> RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken) is not { } endpoint)
            return null;
        if (endpoint.IsLocal)
            throw new InvalidOperationException("The local endpoint can't be removed.");

        db.Endpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        connections.Remove(id);

        // Stack files, and the checkouts of git-backed stacks.
        foreach (var stacks in (string[])[Path.Combine(options.Value.StacksPath, id.ToString(CultureInfo.InvariantCulture)),
                     Path.Combine(options.Value.GitPath, id.ToString(CultureInfo.InvariantCulture))])
        {
            try
            {
                Stacks.StackStore.DeleteTree(stacks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogStacksLeft(logger, ex, stacks);
            }
        }
        LogRemoved(logger, endpoint.Name);
        return endpoint.Name;
    }

    // ---------------------------------------------------------------- validation

    private void SetEnrolment(EngineEndpoint endpoint, AgentToken token)
    {
        endpoint.EnrolmentTokenId = token.Id;
        endpoint.ProtectedEnrolmentSecret = Convert.ToBase64String(_protector.Protect(token.Secret));
        endpoint.EnrolmentExpiresAt = time.GetUtcNow() + EnrolmentLifetime;
    }

    private static void CheckName(string name, List<EndpointInputError> errors)
    {
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
            errors.Add(new("Name", $"Give it a name of up to {MaxNameLength} characters."));
    }

    private static async Task CheckNameIsFreeAsync(PortDbContext db, string name, int? id, List<EndpointInputError> errors, CancellationToken cancellationToken)
    {
        if (errors.Any(e => e.Field == "Name"))
            return;
        var names = await db.Endpoints.Where(e => e.Id != (id ?? 0)).Select(e => e.Name).ToListAsync(cancellationToken);
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            errors.Add(new("Name", $"There's already an endpoint called {name}."));
    }

    /// <summary><c>host</c>, <c>host:port</c> or <c>[v6]:port</c> (a leading <c>https://</c> or <c>tcp://</c> is allowed) → <c>https://host:port</c>.</summary>
    internal static TcpAddress? ParseAgentAddress(string? value, List<EndpointInputError> errors)
    {
        const string field = "Address";
        var text = (value ?? "").Trim();
        foreach (var scheme in (string[])["https://", "tcp://"])
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                text = text[scheme.Length..];
        }
        text = text.TrimEnd('/');

        string host;
        var port = AgentProtocol.DefaultPort;
        string? portText = null;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            host = close > 0 ? text[1..close] : "";
            if (close > 0 && close + 1 < text.Length)
                portText = text[(close + 1)..] is [':', .. var rest] ? rest : "invalid";
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        else
        {
            host = text;
        }

        if (portText is not null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            errors.Add(new(field, "The port must be a number from 1 to 65535."));
            return null;
        }

        host = host.ToLowerInvariant();
        // IPAddress also reads "10" or "10.1" as IPv4; only dotted quads and IPv6 count as addresses.
        var isIp = IPAddress.TryParse(host, out var ip)
                   && (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || host.Count(c => c == '.') == 3);
        if (!isIp && !HostPattern().IsMatch(host))
        {
            errors.Add(new(field, "Use the agent host's name or IP address, optionally with a port: agent.example.com or 192.0.2.10:9001."));
            return null;
        }
        return new TcpAddress(host, port, new DockerTls());
    }

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "Added agent endpoint {Endpoint} at {Address}")]
    private static partial void LogAdded(ILogger logger, string endpoint, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint {Endpoint} waits for a new enrolment at {Address}")]
    private static partial void LogReenrolling(ILogger logger, string endpoint, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed endpoint {Endpoint}")]
    private static partial void LogRemoved(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the stack files in {Path}")]
    private static partial void LogStacksLeft(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pending enrolment of {Endpoint} can't be decrypted; create a new one")]
    private static partial void LogUnreadableEnrolment(ILogger logger, Exception exception, string endpoint);
}
