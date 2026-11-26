using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Audit;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Settings;
using Sockless.Port.Swarm;
using Sockless.Port.Work;

namespace Sockless.Port.Agents;

/// <summary>How an agent's last update went, for its endpoint's page and for not retrying a failure at once.</summary>
/// <param name="Version">The version it was updated to; null for a development build's <c>latest</c>.</param>
public sealed record AgentUpdateResult(string Image, string? Version, bool Succeeded, string Message, DateTimeOffset At, string By);

/// <summary>Who asked for an update: a user, or <see cref="Automatic"/>.</summary>
public sealed record AgentUpdateTrigger(string By, string? UserId = null, string? RemoteAddress = null)
{
    public static AgentUpdateTrigger Automatic { get; } = new("automatic updates");

    public bool IsAutomatic => this == Automatic;
}

/// <summary>
/// Updates agents to the image matching this server, from the server: on an endpoint's page, and
/// automatically for agents older than the server when Settings allow it (the default). An agent
/// in a container is replaced on its host by a helper from the new image (<see cref="AgentUpdate"/>);
/// an agent that's a swarm service gets a service update with the new image, pinned to its digest.
/// Either way the identity stays in the agent's volume, so nothing is enrolled again.
/// </summary>
public sealed partial class AgentUpdates(
    EngineConnections connections,
    RegistryCredentials registries,
    SwarmServices services,
    PortSettings settings,
    AuditLog audit,
    IOptions<PortOptions> options,
    TimeProvider time,
    ILogger<AgentUpdates> logger,
    WorkTracker? work = null,
    Notifications.Alerts? alerts = null) : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);

    /// <summary>An automatic update that failed isn't tried again for the same version before this.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

    /// <summary>How long a container agent has to come back: the helper waits up to two minutes for it to be healthy.</summary>
    internal static TimeSpan ContainerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Swarm rolls a service out node by node, so the endpoint's node may come late.</summary>
    internal static TimeSpan ServiceTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _running = new();
    private readonly ConcurrentDictionary<int, AgentUpdateResult> _results = new();

    public string Image => AgentCommands.DefaultImage(options.Value.AgentImage);

    /// <summary>The version <see cref="Image"/> is, when it names one (not a development build's <c>latest</c>).</summary>
    public string? TargetVersion => AgentCommands.ServerVersion.Contains('-', StringComparison.Ordinal) && options.Value.AgentImage is not { Length: > 0 }
        ? null
        : AgentCommands.ServerVersion;

    public bool IsUpdating(int endpointId) => _running.ContainsKey(endpointId);

    public AgentUpdateResult? LastResult(int endpointId) => _results.GetValueOrDefault(endpointId);

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

    /// <summary>Updates, one at a time, every online agent that's older than the server, when Settings allow it.</summary>
    internal async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        if (!settings.Current.AgentAutoUpdate)
            return;
        foreach (var connection in connections.All)
        {
            if (!IsDue(connection))
                continue;
            if (Problem(connection) is not null || !_running.TryAdd(connection.Id, time.GetUtcNow()))
                continue;
            try
            {
                await RunAsync(connection, AgentUpdateTrigger.Automatic, cancellationToken);
            }
            finally
            {
                _running.TryRemove(connection.Id, out _);
            }
        }
    }

    /// <summary>Whether an automatic update would update this agent now.</summary>
    internal bool IsDue(EngineConnection connection) =>
        connection is { IsAgent: true, Agent.Info.Version: { } version } && connection.Client is not null && AgentCommands.IsOutdated(version)
        && !(_results.GetValueOrDefault(connection.Id) is { Succeeded: false } failed && failed.Image == Image && time.GetUtcNow() - failed.At < RetryAfter);

    /// <summary>Why the endpoint's agent can't be updated from here now, or null.</summary>
    public string? Problem(EngineConnection connection) => connection switch
    {
        { IsAgent: false } => "Only an agent can be updated.",
        { Agent: null } or { Agent.Thumbprint: null } => "The agent isn't enrolled yet.",
        { Client: null } => "The agent isn't connected. It can be updated once it's online.",
        _ when IsUpdating(connection.Id) => "The agent is being updated already.",
        _ => null,
    };

    /// <summary>Starts updating the endpoint's agent in the background. Null when it started, otherwise why not.</summary>
    public string? Start(int endpointId, AgentUpdateTrigger trigger)
    {
        if (connections.Get(endpointId) is not { } connection)
            return "The endpoint was removed.";
        if (Problem(connection) is { } problem)
            return problem;
        if (!_running.TryAdd(endpointId, time.GetUtcNow()))
            return "The agent is being updated already.";
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(connection, trigger, CancellationToken.None);
            }
            finally
            {
                _running.TryRemove(endpointId, out _);
            }
        }, CancellationToken.None);
        return null;
    }

    /// <summary>Updates the agent and records how it went. The caller holds the endpoint's place in <see cref="_running"/>.</summary>
    internal async Task<AgentUpdateResult> RunAsync(EngineConnection connection, AgentUpdateTrigger trigger, CancellationToken cancellationToken)
    {
        var image = Image;
        var target = TargetVersion;
        var from = connection.Agent?.Info?.Version ?? "unknown";
        using var item = work?.Start(WorkKind.AgentUpdate, $"Updating the agent on {connection.Name} to {target ?? image}", connection.Id, connection.Name,
            by: trigger.By);

        string? error;
        string message;
        try
        {
            (error, message) = await UpdateAsync(connection, image, target, item, cancellationToken);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or ObjectDisposedException
                                       or ServiceRejectedException or System.Text.Json.JsonException or InvalidDataException
                                       or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            (error, message) = (EngineErrors.ForCall(ex, connection.Address), "");
        }

        var result = new AgentUpdateResult(image, target, error is null, error ?? message, time.GetUtcNow(), trigger.By);
        _results[connection.Id] = result;
        if (error is null)
        {
            item?.Succeed(message);
            LogUpdated(logger, connection.Name, from, connection.Agent?.Info?.Version ?? "unknown");
        }
        else
        {
            item?.Fail(error);
            LogFailed(logger, connection.Name, error);
            if (alerts is not null)
                await alerts.TaskFailedAsync(connection, $"Couldn't update the agent on {connection.Name}", error, $"/endpoints/{connection.Id}/settings", CancellationToken.None);
        }
        if (trigger.IsAutomatic)
        {
            await audit.WriteAsync(new AuditRecord(trigger.UserId, trigger.By, "Endpoints: update agent", $"{from} to {target ?? image}", connection.Id, connection.Name,
                error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed, error, trigger.RemoteAddress));
        }
        return result;
    }

    /// <returns>An error, or null and how it ended.</returns>
    private async Task<(string? Error, string Message)> UpdateAsync(EngineConnection connection, string image, string? target, WorkItem? item,
        CancellationToken cancellationToken)
    {
        if (connection.Client is not { } client || connection.Agent?.Info is not { } info)
            return ("The agent isn't connected. It can be updated once it's online.", "");

        item?.Describe("Finding the agent's container");
        var found = await FindContainerAsync(client, info, cancellationToken);
        if (found is null)
            return ("Couldn't find the agent's container on its host, so it can't be updated from here. Update it with the command on the endpoint's page.", "");
        var (container, json) = found.Value;
        var startedBefore = info.StartedAt;
        var since = time.GetUtcNow();

        if (container.Config?.Labels?.GetValueOrDefault("com.docker.swarm.service.id") is { } serviceId)
        {
            if (connection.Engine?.Has(EngineCapabilities.SwarmManager) != true)
                return ("The agent is a swarm service, and its node isn't a manager, so it can't change the service. Update it from a manager with the command on the endpoint's page.", "");
            item?.Describe("Updating the agent's service");
            var warnings = await UpdateServiceAsync(client, serviceId, image, cancellationToken);
            foreach (var warning in warnings)
                LogServiceWarning(logger, connection.Name, warning);
            item?.Describe("Waiting for the agent to come back");
            if (!await WaitForNewAgentAsync(connection, startedBefore, since + ServiceTimeout, cancellationToken))
                return ($"The agent didn't come back within {Minutes(ServiceTimeout)} minutes after its service was updated. Check the service's tasks with docker service ps on a manager.", "");
            return Outcome(connection, target);
        }

        // Helpers left by an earlier attempt that timed out said what they had to; one still running is another update.
        var helpers = await client.ListContainersAsync(new Dictionary<string, string[]> { ["label"] = [$"{AgentUpdate.HelperLabel}={AgentUpdate.HelperKind}"] },
            cancellationToken: cancellationToken);
        if (helpers.Any(h => h.State is "running" or "created" or "restarting"))
            return ("Another update of this agent is still running on its host.", "");
        foreach (var leftover in helpers)
            await RemoveHelperAsync(client, leftover.Id);

        item?.Describe($"Pulling {image}");
        var auth = await registries.AuthHeaderForAsync(image, cancellationToken);
        await foreach (var _ in client.PullImageAsync(image, auth, cancellationToken)) { }

        if (HelperBody(JsonNode.Parse(json)!.AsObject(), info, image) is not { } body)
            return ("The agent's engine socket isn't mounted from the host in a way the update can use. Update it with the command on the endpoint's page.", "");

        item?.Describe("Replacing the agent's container");
        var name = AgentUpdate.HelperName(since);
        var helperId = (await client.CreateContainerAsync(name, body, cancellationToken)).Id;
        try
        {
            await client.StartContainerAsync(helperId, cancellationToken);
        }
        catch
        {
            await RemoveHelperAsync(client, helperId);
            throw;
        }

        item?.Describe("Waiting for the agent to come back");
        var (exitCode, output) = await WaitForHelperAsync(connection, helperId, since + ContainerTimeout, cancellationToken);
        if (exitCode is null)
            return ($"The agent didn't come back within {Minutes(ContainerTimeout)} minutes. On its host, docker logs {name} says what happened.", "");
        if (connection.Client is { } after)
            await RemoveHelperAsync(after, helperId);

        if (exitCode != AgentUpdate.Updated)
        {
            var last = output.LastOrDefault() ?? "";
            return (last.Length > 0 ? last : $"The update helper ended with exit code {exitCode.Value.ToString(CultureInfo.InvariantCulture)}.", "");
        }
        return await WaitForNewAgentAsync(connection, startedBefore, time.GetUtcNow() + TimeSpan.FromMinutes(1), cancellationToken)
            ? Outcome(connection, target)
            : ("The agent's container was replaced, but the new agent didn't connect.", "");
    }

    private static (string? Error, string Message) Outcome(EngineConnection connection, string? target)
    {
        var now = connection.Agent?.Info?.Version;
        if (target is not null && now is not null && AgentCommands.IsOutdated(now))
            return ($"The agent was replaced, but still runs {now}. Check that the agent image is version {target}.", "");
        return (null, $"Updated the agent on {connection.Name}{(now is null ? "" : $" to {now}")}.");
    }

    /// <summary>
    /// The agent's own container: by the ids it reports, else (agents from before automatic updates)
    /// the one running container from the agent image, preferring the install command's name.
    /// </summary>
    internal static async Task<(ContainerInspect Container, string Json)?> FindContainerAsync(DockerClient client, AgentInfo info, CancellationToken cancellationToken)
    {
        foreach (var id in info.ContainerIds ?? [])
        {
            try
            {
                return await client.InspectContainerAsync(id, cancellationToken);
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
            }
        }

        var filters = new Dictionary<string, string[]>
        {
            ["label"] = [$"{AgentUpdate.ImageTitleLabel}={AgentUpdate.ImageTitle}"],
            ["status"] = ["running"],
        };
        var candidates = (await client.ListContainersAsync(filters, all: false, cancellationToken))
            .Where(c => c.Labels?.ContainsKey(AgentUpdate.HelperLabel) != true)
            .ToList();
        var chosen = candidates.Count == 1 ? candidates[0]
            : candidates.FirstOrDefault(c => c.Names?.Any(n => n.TrimStart('/') == AgentCommands.ContainerName) == true)
              ?? candidates.FirstOrDefault(c => c.Labels?.GetValueOrDefault("com.docker.swarm.service.name") == AgentCommands.ContainerName);
        return chosen is null ? null : await client.InspectContainerAsync(chosen.Id, cancellationToken);
    }

    /// <summary>
    /// The helper that replaces the agent: the new image running <c>update</c>, with the agent's
    /// engine socket, user, groups and security options (so it may use the socket as the agent
    /// does), no network when the engine is a socket, and no health check. Null when the socket
    /// isn't mounted from the host.
    /// </summary>
    internal static JsonObject? HelperBody(JsonObject inspect, AgentInfo info, string image)
    {
        var id = inspect["Id"]?.GetValue<string>() ?? "";
        DockerAddress engine;
        try
        {
            engine = DockerAddress.Parse(info.Engine ?? "unix:///var/run/docker.sock");
        }
        catch (FormatException)
        {
            return null;
        }

        var host = new JsonObject();
        if (engine is UnixSocketAddress socket)
        {
            var source = (inspect["Mounts"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(m => m["Destination"]?.GetValue<string>() == socket.Path)?["Source"]?.GetValue<string>();
            if (source is not { Length: > 0 })
                return null;
            host["Binds"] = new JsonArray((JsonNode)$"{source}:{socket.Path}");
            host["NetworkMode"] = "none";
        }
        else if (inspect["HostConfig"]?["NetworkMode"]?.DeepClone() is { } network)
            host["NetworkMode"] = network;
        foreach (var key in (string[])["GroupAdd", "SecurityOpt", "UsernsMode"])
        {
            if (inspect["HostConfig"]?[key] is { } value)
                host[key] = value.DeepClone();
        }

        var body = new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray([.. AgentUpdate.Arguments(id, image).Select(a => (JsonNode)a)]),
            ["Env"] = new JsonArray((JsonNode)$"{AgentUpdate.EngineVariable}={engine}"),
            ["Labels"] = new JsonObject
            {
                [AgentUpdate.HelperLabel] = AgentUpdate.HelperKind,
                [AgentUpdate.TargetLabel] = id,
                [AgentUpdate.ImageLabel] = image,
            },
            ["Healthcheck"] = new JsonObject { ["Test"] = new JsonArray((JsonNode)"NONE") },
            ["HostConfig"] = host,
        };
        if (inspect["Config"]?["User"]?.GetValue<string>() is { Length: > 0 } user)
            body["User"] = user;
        return body;
    }

    /// <summary>Points the agent's service at the new image (pinned to its digest), rolling back by itself if the new tasks fail.</summary>
    private async Task<IReadOnlyList<string>> UpdateServiceAsync(DockerClient client, string serviceId, string image, CancellationToken cancellationToken)
    {
        var (service, json) = await client.InspectServiceAsync(serviceId, cancellationToken);
        if (JsonNode.Parse(json)?["Spec"]?.DeepClone() is not JsonObject spec || spec["TaskTemplate"]?["ContainerSpec"] is not JsonObject container)
            throw new ServiceRejectedException("The agent's service has no container spec.");
        container["Image"] = image;
        var update = spec["UpdateConfig"] as JsonObject ?? (JsonObject)(spec["UpdateConfig"] = new JsonObject());
        update["FailureAction"] = "rollback";
        return (await services.UpdateAsync(client, service, spec, cancellationToken)).Warnings;
    }

    /// <summary>Waits until the endpoint is online with an agent that started after <paramref name="startedBefore"/>.</summary>
    private async Task<bool> WaitForNewAgentAsync(EngineConnection connection, DateTimeOffset? startedBefore, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (connection.Client is not null && connection.Agent?.Info is { } info && (startedBefore is null || info.StartedAt > startedBefore))
                return true;
            if (time.GetUtcNow() >= deadline)
                return false;
            await Task.Delay(Poll, time, cancellationToken);
        }
    }

    /// <summary>Waits until the helper has ended, reading it through whichever agent is up. Null exit code on timeout.</summary>
    private async Task<(int? ExitCode, IReadOnlyList<string> Output)> WaitForHelperAsync(EngineConnection connection, string helperId, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (time.GetUtcNow() < deadline)
        {
            if (connection.Client is { } client)
            {
                try
                {
                    var (helper, _) = await client.InspectContainerAsync(helperId, cancellationToken);
                    if (helper.State is { Running: false, Restarting: false } state && state.Status != "created")
                    {
                        List<string> output = [];
                        await foreach (var line in client.StreamLogsAsync(helperId, new LogOptions { Tail = 20 }, cancellationToken))
                        {
                            if (line.Text.Trim() is { Length: > 0 } text)
                                output.Add(text);
                        }
                        return (state.ExitCode, output);
                    }
                }
                catch (DockerApiException ex) when (ex.IsNotFound)
                {
                    return (AgentUpdate.Broken, ["The update helper was removed before it said how the update went."]);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or ObjectDisposedException
                                               or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // The agent is going away or coming back; ask again.
                }
            }
            await Task.Delay(Poll, time, cancellationToken);
        }
        return (null, []);
    }

    private async Task RemoveHelperAsync(DockerClient client, string helperId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // With its volumes: the image's VOLUME gave it an empty one of its own.
            await client.RemoveContainerAsync(helperId, force: true, removeVolumes: true, cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            LogHelperLeft(logger, helperId[..Math.Min(12, helperId.Length)], ex.Message);
        }
    }

    private static string Minutes(TimeSpan span) => span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated the agent of endpoint {Endpoint} from {From} to {To}")]
    private static partial void LogUpdated(ILogger logger, string endpoint, string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the agent of endpoint {Endpoint}: {Error}")]
    private static partial void LogFailed(ILogger logger, string endpoint, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating the agent service of endpoint {Endpoint}: {Warning}")]
    private static partial void LogServiceWarning(ILogger logger, string endpoint, string warning);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't remove the agent update helper {Container}: {Message}")]
    private static partial void LogHelperLeft(ILogger logger, string container, string message);
}
