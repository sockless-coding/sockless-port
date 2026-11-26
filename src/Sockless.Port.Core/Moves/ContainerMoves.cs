using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Pods;
using Sockless.Port.Work;

namespace Sockless.Port.Moves;

/// <summary>A container move couldn't start. The message is for people.</summary>
public sealed class ContainerMoveRejectedException(string message) : Exception(message);

/// <summary>
/// What a move of a container would do and what stands in its way, from the checks
/// <see cref="ContainerMoves.CheckAsync"/> runs on both endpoints.
/// </summary>
/// <param name="Name">The container's name, which it keeps on the target.</param>
/// <param name="Running">It runs now, so it's started on the target too.</param>
/// <param name="Networks">User-defined networks it's on that the target lacks: they're created there like on the source.</param>
public sealed record ContainerMovePlan(string Name, bool Running, IReadOnlyList<MoveFinding> Findings, IReadOnlyList<MoveVolume> Volumes,
    IReadOnlyList<string> Images, IReadOnlyList<string> Networks)
{
    public bool CanMove => Findings.All(f => f.Severity != MoveSeverity.Blocker);

    /// <summary>A container can be removed, stopped or kept running after.</summary>
    public static IReadOnlyList<MoveSourceAfter> Afters { get; } = [MoveSourceAfter.Remove, MoveSourceAfter.Stop, MoveSourceAfter.KeepRunning];
}

/// <summary>
/// Moves a container that isn't part of a stack, pod or swarm service to another endpoint: the
/// networks it needs, the data of its named volumes when asked, its image, then a new container
/// from its raw inspect configuration (as recreating does, so settings the UI doesn't model
/// survive). The source is stopped while its volumes are copied, and started again if the move
/// fails. Runs in the background and reports to the list of background work.
/// </summary>
public sealed class ContainerMoves(MoveTransfers transfers, ILogger<ContainerMoves> logger, WorkTracker? work = null, Notifications.Alerts? alerts = null)
    : IDisposable
{
    /// <summary>Networks every engine has, which are never created.</summary>
    private static readonly HashSet<string> BuiltInNetworks = new(StringComparer.Ordinal) { "bridge", "host", "none", "default", "podman" };

    private readonly ConcurrentDictionary<(int Endpoint, string Name), bool> _moving = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Is the container (by name) being moved away from or onto this endpoint?</summary>
    public bool IsMoving(int endpointId, string name) => _moving.ContainsKey((endpointId, name));

    /// <summary>Checks both endpoints for what the move needs. Reads only.</summary>
    /// <param name="containerId">The container's ID or name on <paramref name="source"/>.</param>
    public async Task<ContainerMovePlan> CheckAsync(EngineConnection source, EngineConnection target, string containerId, CancellationToken cancellationToken = default)
    {
        var findings = new MoveFindings();
        if (source.Client is not { } sourceClient)
        {
            findings.Block($"{source.Name} is offline, so the container can't be read or stopped there.");
            return new ContainerMovePlan(containerId, false, findings.All, [], [], []);
        }
        var (container, json) = await sourceClient.InspectContainerAsync(containerId, cancellationToken);
        var name = container.DisplayName;
        var running = container.State is { Running: true } or { Restarting: true };
        ContainerMovePlan Plan(MoveContents? contents = null, IReadOnlyList<string>? networks = null) =>
            new(name, running, findings.All, contents?.Volumes ?? [], contents?.Images ?? [], networks ?? []);

        var labels = container.Config?.Labels;
        if (source.Id == target.Id)
            findings.Block($"{name} is on {target.Name} already. Choose another endpoint.");
        if (IsMoving(source.Id, name) || IsMoving(target.Id, name))
            findings.Block($"{name} is being moved already. Wait for it to finish.");
        if (ContainerLabels.IsHelper(labels))
            findings.Block($"{name} is a helper Sockless Port started for itself. It isn't moved.");
        if (SelfContainer.Is(container.Id))
            findings.Block("Sockless Port runs in this container, so it can't move it.");
        if (ContainerLabels.SwarmServiceName(labels) is { } service)
            findings.Block($"{name} is a task of the swarm service {service}. The swarm decides where it runs: change the service instead.");
        else if (ContainerLabels.Stack(labels) is { } stack)
            findings.Block($"{name} belongs to the stack {stack}. Move the stack instead, so its containers stay together.");
        if ((await PodMembership.LoadAsync(sourceClient, source.Engine, cancellationToken)).Of(container.Id) is { } pod)
            findings.Block($"{name} is in the pod {pod.Pod.Name}, which Docker's API can't put a container in on another endpoint.");
        var mode = container.HostConfig?.NetworkMode ?? "default";
        if (mode.StartsWith("container:", StringComparison.Ordinal))
            findings.Block($"{name} shares the network of another container ({mode["container:".Length..]}), which doesn't move with it.");

        if (target.Client is not { } targetClient)
        {
            findings.Block($"{target.Name} is offline.");
            return Plan();
        }
        try
        {
            await targetClient.InspectContainerAsync(name, cancellationToken);
            findings.Block($"There's already a container named {name} on {target.Name}. Rename or remove one of them first.");
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
        }
        if (source.Engine?.Kind is { } from && target.Engine?.Kind is { } to && from != to)
            findings.Note($"From {from} to {to}: settings {to} doesn't know are left out by it.");

        var networks = await CheckNetworksAsync(sourceClient, target, targetClient, JsonNode.Parse(json)?["NetworkSettings"]?["Networks"] as JsonObject, findings,
            cancellationToken);
        var summary = (await sourceClient.ListContainersAsync(new Dictionary<string, string[]> { ["id"] = [container.Id] }, all: true, cancellationToken))
            .FirstOrDefault(c => c.Id == container.Id);
        var contents = summary is null
            ? new MoveContents([], [])
            : await transfers.CheckContainersAsync(source, sourceClient, target, targetClient, [summary], name, findings, cancellationToken);
        return Plan(contents, networks);
    }

    /// <summary>The user-defined networks the target lacks; those that can't simply be created there are findings.</summary>
    private static async Task<IReadOnlyList<string>> CheckNetworksAsync(DockerClient sourceClient, EngineConnection target, DockerClient targetClient,
        JsonObject? attached, MoveFindings findings, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        foreach (var (network, settings) in attached ?? [])
        {
            if (BuiltInNetworks.Contains(network))
                continue;
            if (settings?["IPAMConfig"] is JsonObject { Count: > 0 })
                findings.Note($"It has a fixed address on {network}: that address must be free there.");
            try
            {
                await targetClient.InspectNetworkAsync(network, cancellationToken);
                continue;
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
            }
            var (source, _) = await sourceClient.InspectNetworkAsync(network, cancellationToken);
            switch (source.Driver)
            {
                case "overlay":
                    findings.Block($"{network} is a swarm network. Attach {target.Name} to that swarm, or create a network named {network} there first.");
                    break;
                case "macvlan" or "ipvlan":
                    findings.Warn($"{network} is a {source.Driver} network: it's created on {target.Name} with the same options, so the host interface they name must exist there.");
                    missing.Add(network);
                    break;
                default:
                    findings.Note($"The network {network} isn't on {target.Name}: it's created there like on the source.");
                    missing.Add(network);
                    break;
            }
        }
        return missing;
    }

    /// <summary>Checks again, then starts the move in the background.</summary>
    /// <param name="by">Who started it, for the list of background work.</param>
    /// <exception cref="ContainerMoveRejectedException">A check failed (the first blocker says why).</exception>
    public async Task<ContainerMovePlan> StartAsync(EngineConnection source, EngineConnection target, string containerId, MoveOptions options, string? by = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await CheckAsync(source, target, containerId, cancellationToken);
        if (plan.Findings.FirstOrDefault(f => f.Severity == MoveSeverity.Blocker) is { } blocker)
            throw new ContainerMoveRejectedException(blocker.Message);
        if (!_moving.TryAdd((source.Id, plan.Name), true))
            throw new ContainerMoveRejectedException($"{plan.Name} is being moved already. Wait for it to finish.");
        if (!_moving.TryAdd((target.Id, plan.Name), true))
        {
            _moving.TryRemove((source.Id, plan.Name), out _);
            throw new ContainerMoveRejectedException($"{plan.Name} is being moved already. Wait for it to finish.");
        }

        var item = work?.Start(WorkKind.MoveContainer, $"Moving {plan.Name} to {target.Name}", target.Id, target.Name, plan.Name, by);
        _ = RunInBackgroundAsync(item, source, target, containerId, plan, options);
        return plan;
    }

    private async Task RunInBackgroundAsync(WorkItem? item, EngineConnection source, EngineConnection target, string containerId, ContainerMovePlan plan,
        MoveOptions options)
    {
        await Task.Yield();
        var token = _stopping.Token;
        using (item)
        {
            try
            {
                void Log(string line)
                {
                    item?.Describe(line.Length > 200 ? line[..200] + "…" : line);
                    logger.LogInformation("Moving container {Container} to endpoint {Endpoint}: {Line}", plan.Name, target.Id, line);
                }
                var outcome = await RunAsync(Log, source, target, containerId, plan, options, token);
                if (outcome.Error is { } error)
                {
                    item?.Fail(error);
                    if (alerts is not null)
                    {
                        await alerts.TaskFailedAsync(source, $"Moving the container {plan.Name} to {target.Name} failed", error,
                            $"/endpoints/{source.Id}/containers/{Uri.EscapeDataString(plan.Name)}", CancellationToken.None);
                    }
                }
                else
                {
                    item?.Succeed(outcome.Message!);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                item?.Canceled();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moving container {Container} to endpoint {Endpoint} failed unexpectedly", plan.Name, target.Id);
                item?.Fail("Something went wrong on the server. The log has the details.");
            }
            finally
            {
                _moving.TryRemove((source.Id, plan.Name), out _);
                _moving.TryRemove((target.Id, plan.Name), out _);
            }
        }
    }

    /// <returns>How it ended, for people: a message, or an error.</returns>
    internal async Task<(string? Message, string? Error)> RunAsync(Action<string> log, EngineConnection source, EngineConnection target, string containerId,
        ContainerMovePlan plan, MoveOptions options, CancellationToken cancellationToken)
    {
        var name = plan.Name;
        var sourceClient = source.Client ?? throw new IOException($"{source.Name} went offline.");
        var targetClient = target.Client ?? throw new IOException($"{target.Name} went offline.");
        var (container, json) = await sourceClient.InspectContainerAsync(containerId, cancellationToken);
        var (body, extraNetworks) = ContainerRecreator.BuildCreateBody(JsonNode.Parse(json)!.AsObject());
        var running = container.State is { Running: true } or { Restarting: true };

        var copying = options.CopyVolumes ? plan.Volumes.Where(v => v.Copyable).ToList() : [];
        var stopped = false;
        string? created = null;
        try
        {
            if (copying.Count > 0 && running)
            {
                log($"Stopping {name} on {source.Name}, so its volumes don't change while they're copied.");
                await sourceClient.StopContainerAsync(container.Id, cancellationToken: cancellationToken);
                stopped = true;
            }

            foreach (var network in plan.Networks)
            {
                var (original, _) = await sourceClient.InspectNetworkAsync(network, cancellationToken);
                log($"Creating the network {network} on {target.Name}.");
                await targetClient.CreateNetworkAsync(new NetworkCreateRequest
                {
                    Name = network,
                    Driver = original.Driver,
                    Internal = original.Internal,
                    Attachable = original.Attachable,
                    EnableIpv6 = original.EnableIpv6,
                    Ipam = original.Ipam,
                    Options = original.Options,
                    Labels = original.Labels,
                }, cancellationToken);
            }

            foreach (var volume in copying)
                await transfers.CopyVolumeAsync(log, source, target, volume.Name, cancellationToken);
            foreach (var image in plan.Images)
                await transfers.EnsureImageAsync(log, source, target, image, cancellationToken);

            log($"Creating {name} on {target.Name}.");
            created = (await targetClient.CreateContainerAsync(name, body, cancellationToken)).Id;
            foreach (var (network, endpoint) in extraNetworks)
                await targetClient.ConnectNetworkAsync(network, created, endpoint, cancellationToken);
            if (running)
            {
                log($"Starting {name} on {target.Name}.");
                await targetClient.StartContainerAsync(created, cancellationToken);
            }
        }
        catch (Exception ex) when (MoveTransfers.IsEngineFailure(ex, cancellationToken))
        {
            var error = $"Moving {name} failed: {EngineErrors.Sentence(ex.Message)}";
            // Not cancellable: putting things back must finish.
            if (created is not null)
                await TryAsync(() => targetClient.RemoveContainerAsync(created, force: true));
            if (stopped)
            {
                log($"Starting {name} on {source.Name} again.");
                if (!await TryAsync(() => sourceClient.StartContainerAsync(container.Id)))
                    return (null, $"{error} Starting it on {source.Name} again failed too.");
                error += $" It runs on {source.Name} again.";
            }
            var left = copying.Count > 0 || plan.Networks.Count > 0 ? $" The volumes and networks made on {target.Name} are kept." : "";
            return (null, error + left);
        }

        var moved = $"{name} runs on {target.Name} now";
        switch (options.After)
        {
            case MoveSourceAfter.Remove:
                log($"Removing {name} from {source.Name}. Its volumes are kept there.");
                if (!await TryAsync(() => sourceClient.RemoveContainerAsync(container.Id, force: true, removeVolumes: false, cancellationToken)))
                    return (null, $"{moved}, but removing it from {source.Name} failed. Remove it there yourself.");
                return ($"Moved {name} to {target.Name}. It's removed from {source.Name}; its volumes are still there.", null);
            case MoveSourceAfter.Stop:
                if (running && !stopped && !await TryAsync(() => sourceClient.StopContainerAsync(container.Id, cancellationToken: cancellationToken)))
                    return (null, $"{moved}, but stopping it on {source.Name} failed.");
                return ($"Moved {name} to {target.Name}. It's {(running ? "stopped" : "kept")} on {source.Name}: remove it there once the move is confirmed.", null);
            default:
                if (stopped && !await TryAsync(() => sourceClient.StartContainerAsync(container.Id, cancellationToken)))
                    return (null, $"{moved}, but starting it again on {source.Name} failed.");
                return ($"Copied {name} to {target.Name}. It {(running ? "runs" : "is")} on both endpoints now.", null);
        }
    }

    private async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            logger.LogWarning(ex, "A step of a container move failed");
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
