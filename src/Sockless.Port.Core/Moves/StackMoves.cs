using Microsoft.Extensions.Logging;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;

namespace Sockless.Port.Moves;

/// <summary>How much a check's finding matters: a blocker stops the move, a warning is to read first.</summary>
public enum MoveSeverity
{
    Note,
    Warning,
    Blocker,
}

public sealed record MoveFinding(MoveSeverity Severity, string Message);

/// <summary>A named volume that moves, and whether its data can be copied.</summary>
/// <param name="Problem">Why it can't be copied; null when it can.</param>
public sealed record MoveVolume(string Name, string? Problem)
{
    public bool Copyable => Problem is null;
}

/// <summary>What happens on the endpoint a stack or container comes from, once it runs on the target.</summary>
public enum MoveSourceAfter
{
    /// <summary>Take it down and forget it there (its volumes are kept).</summary>
    Remove,

    /// <summary>Leave it stopped there until the move is confirmed.</summary>
    Stop,

    /// <summary>Leave it running there too: a copy.</summary>
    KeepRunning,
}

/// <param name="CopyVolumes">Stop the source and copy the data of its copyable volumes.</param>
public sealed record MoveOptions(bool CopyVolumes, MoveSourceAfter After);

/// <summary>
/// What a move of a stack would do and what stands in its way, from the checks
/// <see cref="StackMoves.CheckAsync"/> runs on both endpoints.
/// </summary>
/// <param name="Deployed">The stack runs on the source: it's deployed on the target too. Otherwise only its files move.</param>
/// <param name="Images">The images its containers or services run.</param>
/// <param name="Afters">What may happen to the source after (swarm stacks can't be stopped).</param>
public sealed record MovePlan(string StackName, StackKind Kind, bool Deployed, IReadOnlyList<MoveFinding> Findings, IReadOnlyList<MoveVolume> Volumes,
    IReadOnlyList<string> Images, IReadOnlyList<MoveSourceAfter> Afters)
{
    public bool CanMove => Findings.All(f => f.Severity != MoveSeverity.Blocker);
}

/// <summary>
/// Moves a stack Sockless Port manages to another endpoint: its files (and git settings), the
/// data of its named volumes when asked, the images the target can't pull, then a deploy there.
/// The source is stopped while its volumes are copied, and started again if the move fails.
/// Runs as a stack job (<see cref="StackJobs.StartMove"/>), so both stacks are busy meanwhile.
/// </summary>
public sealed class StackMoves(StackStore store, StackJobs jobs, ComposeCli compose, DockerCli docker, MoveTransfers transfers, ILogger<StackMoves> logger)
{
    /// <summary>Checks both endpoints for what the move needs. Reads only.</summary>
    public async Task<MovePlan> CheckAsync(EngineConnection source, EngineConnection target, string name, CancellationToken cancellationToken = default)
    {
        var findings = new MoveFindings();
        MovePlan Plan(StackKind kind, bool deployed = false, MoveContents? contents = null) =>
            new(name, kind, deployed, findings.All, contents?.Volumes ?? [], contents?.Images ?? [],
                kind == StackKind.Swarm ? [MoveSourceAfter.Remove, MoveSourceAfter.KeepRunning] : [MoveSourceAfter.Remove, MoveSourceAfter.Stop, MoveSourceAfter.KeepRunning]);

        var definition = await store.GetAsync(source.Id, name, cancellationToken);
        if (definition is null || !store.Files.Exists(source.Id, name))
        {
            findings.Block($"Sockless Port doesn't manage a stack named {name} on {source.Name}. Adopt it first, then move it.");
            return Plan(StackKind.Compose);
        }
        var kind = definition.Kind;
        if (source.Id == target.Id)
            findings.Block($"{name} is on {target.Name} already. Choose another endpoint.");
        if (jobs.IsBusy(source.Id, name) || jobs.IsBusy(target.Id, name))
            findings.Block($"Something is already running on {name}. Wait for it to finish.");
        if (await store.GetAsync(target.Id, name, cancellationToken) is not null)
            findings.Block($"There's already a stack named {name} on {target.Name}. Rename or remove one of them first.");
        try
        {
            if (kind == StackKind.Swarm)
                await docker.FindAsync(cancellationToken);
            else
                await compose.FindAsync(cancellationToken);
        }
        catch (ComposeUnavailableException ex)
        {
            findings.Block(ex.Message);
        }
        if (source.Client is not { } sourceClient)
        {
            findings.Block($"{source.Name} is offline, so {name} can't be stopped or read there.");
            return Plan(kind);
        }
        if (target.Client is not { } targetClient)
        {
            findings.Block($"{target.Name} is offline.");
            return Plan(kind);
        }

        if (kind == StackKind.Swarm)
        {
            if (target.Engine?.Has(EngineCapabilities.SwarmManager) != true)
                findings.Block($"{name} is a swarm stack, and {target.Name} isn't a swarm manager.");
            else if (source.Engine?.SwarmClusterId is { } cluster && cluster == target.Engine.SwarmClusterId)
                findings.Block($"{target.Name} manages the same swarm as {source.Name}: {name} already runs there.");

            var stack = source.Engine?.Has(EngineCapabilities.SwarmManager) == true
                ? SwarmInventory.Stacks(await sourceClient.ListServicesAsync(StackJobs.StackFilter(name), cancellationToken)).GetValueOrDefault(name)
                : null;
            var deployed = stack is { Services.Count: > 0 };
            findings.Note(deployed
                ? "Swarm volumes live on the nodes, so their data isn't copied. Copy it yourself if the services need it."
                : $"{name} isn't deployed on {source.Name}: only its files move.");
            // The nodes pull them as the services start; the digest swarm pins is left out.
            return Plan(StackKind.Swarm, deployed, new MoveContents([],
                [.. (stack?.Services ?? []).Select(s => s.Image).OfType<string>().Select(i => i.Split('@')[0]).Distinct(StringComparer.Ordinal)]));
        }

        var project = await ProjectAsync(sourceClient, name, cancellationToken);
        if (project.IsSelf)
            findings.Block($"Sockless Port itself runs in {name}, so it can't move it.");
        if ((await ProjectAsync(targetClient, name, cancellationToken)).Containers.Count > 0)
            findings.Block($"A Compose project named {name} already runs on {target.Name}. Take it down there first.");
        if (project.Containers.Count == 0)
        {
            findings.Note($"{name} isn't deployed on {source.Name}: only its files move, and it isn't deployed on {target.Name} either.");
            return Plan(StackKind.Compose);
        }
        var contents = await transfers.CheckContainersAsync(source, sourceClient, target, targetClient, project.Containers, "the stack", findings, cancellationToken);
        return Plan(StackKind.Compose, true, contents);
    }

    /// <summary>Checks again, then starts the move as a job of the target's stack.</summary>
    /// <exception cref="StackJobRejectedException">A check failed (the first blocker says why), or a side is busy.</exception>
    public async Task<(StackJob Job, MovePlan Plan)> StartAsync(EngineConnection source, EngineConnection target, string name, MoveOptions options,
        CancellationToken cancellationToken = default)
    {
        var plan = await CheckAsync(source, target, name, cancellationToken);
        if (plan.Findings.FirstOrDefault(f => f.Severity == MoveSeverity.Blocker) is { } blocker)
            throw new StackJobRejectedException(blocker.Message);
        if (!plan.Afters.Contains(options.After))
            throw new StackJobRejectedException("Swarm stacks can't be left stopped: remove the source, or keep it running.");
        var job = jobs.StartMove(source, target, name, job => RunAsync(job, source, target, plan, options));
        return (job, plan);
    }

    private async Task<StackJobStatus> RunAsync(StackJob job, EngineConnection source, EngineConnection target, MovePlan plan, MoveOptions options)
    {
        var name = plan.StackName;
        var token = job.Token;
        job.AppendLine($"Copying the files of {name} from {source.Name} to {target.Name}.");
        try
        {
            await store.CopyAsync(source.Id, target.Id, name, token);
        }
        catch (StackRejectedException ex)
        {
            return new StackJobFailed(ex.Message, []);
        }

        var stopped = false;
        if (plan.Deployed)
        {
            var copying = plan.Kind == StackKind.Compose && options.CopyVolumes ? plan.Volumes.Where(v => v.Copyable).ToList() : [];
            if (copying.Count > 0)
            {
                job.AppendLine($"Stopping {name} on {source.Name}, so its volumes don't change while they're copied.");
                if (await jobs.RunWithinAsync(job, source, plan.Kind, StackOperation.Stop) is StackJobFailed failed)
                    return await UndoAsync(job, source, target, plan, stopped: false, failed.Error);
                stopped = true;
                foreach (var volume in copying)
                {
                    try
                    {
                        await transfers.CopyVolumeAsync(job.AppendLine, source, target, volume.Name, token);
                    }
                    catch (Exception ex) when (MoveTransfers.IsEngineFailure(ex, token))
                    {
                        return await UndoAsync(job, source, target, plan, stopped, $"Couldn't copy the volume {volume.Name}: {EngineErrors.Sentence(ex.Message)}");
                    }
                }
            }

            // Swarm nodes pull their own images (with the registry credentials the deploy passes).
            if (plan.Kind == StackKind.Compose)
            {
                foreach (var image in plan.Images)
                    await transfers.EnsureImageAsync(job.AppendLine, source, target, image, token);
            }

            job.AppendLine($"Deploying {name} on {target.Name}.");
            if (await jobs.RunWithinAsync(job, target, plan.Kind, StackOperation.Deploy) is StackJobFailed deployFailed)
                return await UndoAsync(job, source, target, plan, stopped, deployFailed.Error, deployFailed.Hints);
        }

        return await FinishSourceAsync(job, source, target, plan, options, stopped);
    }

    /// <summary>What happens to the source once the target runs the stack.</summary>
    private async Task<StackJobStatus> FinishSourceAsync(StackJob job, EngineConnection source, EngineConnection target, MovePlan plan, MoveOptions options,
        bool stopped)
    {
        var name = plan.StackName;
        var moved = plan.Deployed ? $"{name} runs on {target.Name} now." : $"The files of {name} are on {target.Name} now.";
        switch (options.After)
        {
            case MoveSourceAfter.Remove:
                await store.MoveWebhookAsync(source.Id, target.Id, name, CancellationToken.None);
                job.AppendLine($"Removing {name} from {source.Name}. Its volumes are kept there.");
                if (await jobs.RunWithinAsync(job, source, plan.Kind, StackOperation.Remove, new StackJobOptions(RemoveVolumes: false, Down: plan.Deployed))
                    is StackJobFailed removeFailed)
                {
                    return new StackJobFailed($"{moved} But removing it from {source.Name} failed: {removeFailed.Error}", removeFailed.Hints);
                }
                return new StackJobSucceeded(plan.Deployed
                    ? $"Moved {name} to {target.Name}. It's removed from {source.Name}; its volumes are still there."
                    : $"Moved the files of {name} to {target.Name}, and removed them from {source.Name}.");

            case MoveSourceAfter.Stop when plan.Deployed && !stopped:
                job.AppendLine($"Stopping {name} on {source.Name}.");
                if (await jobs.RunWithinAsync(job, source, plan.Kind, StackOperation.Stop) is StackJobFailed stopFailed)
                    return new StackJobFailed($"{moved} But stopping it on {source.Name} failed: {stopFailed.Error}", stopFailed.Hints);
                return new StackJobSucceeded($"Moved {name} to {target.Name}. It's stopped on {source.Name}: remove it there once the move is confirmed.");

            case MoveSourceAfter.Stop:
                return new StackJobSucceeded($"Moved {name} to {target.Name}. {(plan.Deployed ? $"It's stopped on {source.Name}" : $"Its files stay on {source.Name} too")}: remove it there once the move is confirmed.");

            case MoveSourceAfter.KeepRunning when stopped:
                job.AppendLine($"Starting {name} on {source.Name} again.");
                if (await jobs.RunWithinAsync(job, source, plan.Kind, StackOperation.Start) is StackJobFailed startFailed)
                    return new StackJobFailed($"{moved} But starting it again on {source.Name} failed: {startFailed.Error}", startFailed.Hints);
                return new StackJobSucceeded($"Copied {name} to {target.Name}. It runs on both endpoints now.");

            default:
                return new StackJobSucceeded(plan.Deployed ? $"Copied {name} to {target.Name}. It runs on both endpoints now." : $"Copied the files of {name} to {target.Name}.");
        }
    }

    /// <summary>A step failed: the source runs again as it did, and the target keeps the files, not deployed, to look into.</summary>
    private async Task<StackJobStatus> UndoAsync(StackJob job, EngineConnection source, EngineConnection target, MovePlan plan, bool stopped, string error,
        IReadOnlyList<string>? hints = null)
    {
        var name = plan.StackName;
        var after = $"Its files stay on {target.Name}, not deployed: fix the problem and deploy them there, or remove them.";
        if (stopped)
        {
            job.AppendLine($"Starting {name} on {source.Name} again.");
            try
            {
                if (await jobs.RunWithinAsync(job, source, plan.Kind, StackOperation.Start) is StackJobFailed startFailed)
                    return new StackJobFailed($"{error} Starting {name} on {source.Name} again failed too: {startFailed.Error}", hints ?? []);
            }
            catch (Exception ex) when (ex is IOException or ComposeUnavailableException)
            {
                logger.LogWarning(ex, "Couldn't start {Stack} again on endpoint {Endpoint} after a failed move", name, source.Id);
                return new StackJobFailed($"{error} Starting {name} on {source.Name} again failed too: {EngineErrors.Sentence(ex.Message)}", hints ?? []);
            }
            after = $"{name} runs on {source.Name} again. {after}";
        }
        return new StackJobFailed($"{error} {after}", hints ?? []);
    }

    private static async Task<ComposeProject> ProjectAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        var containers = await client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{ContainerLabels.ComposeProject}={name}"] }, all: true, cancellationToken);
        return StackInventory.Projects(containers).GetValueOrDefault(name) ?? ComposeProject.Empty(name);
    }
}
