using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;
using Sockless.Port.Volumes;

namespace Sockless.Port.Moves;

/// <summary>How much a check's finding matters: a blocker stops the move, a warning is to read first.</summary>
public enum MoveSeverity
{
    Note,
    Warning,
    Blocker,
}

public sealed record MoveFinding(MoveSeverity Severity, string Message);

/// <summary>A named volume the stack's containers use, and whether its data can be copied.</summary>
/// <param name="Problem">Why it can't be copied; null when it can.</param>
public sealed record MoveVolume(string Name, string? Problem)
{
    public bool Copyable => Problem is null;
}

/// <summary>What happens to the stack on the endpoint it comes from, once it runs on the target.</summary>
public enum MoveSourceAfter
{
    /// <summary>Take it down and forget it there (its volumes are kept).</summary>
    Remove,

    /// <summary>Leave it stopped there, files and all, until the move is confirmed.</summary>
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
public sealed class StackMoves(StackStore store, StackJobs jobs, ComposeCli compose, DockerCli docker, VolumeBrowser volumes,
    Registries.RegistryCredentials registries, ILogger<StackMoves> logger)
{
    /// <summary>How often a volume copy says how far it is.</summary>
    internal const long ProgressEvery = 256L * 1024 * 1024;

    /// <summary>Checks both endpoints for what the move needs. Reads only.</summary>
    public async Task<MovePlan> CheckAsync(EngineConnection source, EngineConnection target, string name, CancellationToken cancellationToken = default)
    {
        var findings = new List<MoveFinding>();
        void Block(string message) => findings.Add(new(MoveSeverity.Blocker, message));
        void Warn(string message) => findings.Add(new(MoveSeverity.Warning, message));
        void Note(string message) => findings.Add(new(MoveSeverity.Note, message));
        MovePlan Plan(StackKind kind, bool deployed = false, IReadOnlyList<MoveVolume>? found = null, IReadOnlyList<string>? images = null) =>
            new(name, kind, deployed, findings, found ?? [], images ?? [],
                kind == StackKind.Swarm ? [MoveSourceAfter.Remove, MoveSourceAfter.KeepRunning] : [MoveSourceAfter.Remove, MoveSourceAfter.Stop, MoveSourceAfter.KeepRunning]);

        var definition = await store.GetAsync(source.Id, name, cancellationToken);
        if (definition is null || !store.Files.Exists(source.Id, name))
        {
            Block($"Sockless Port doesn't manage a stack named {name} on {source.Name}. Adopt it first, then move it.");
            return Plan(StackKind.Compose);
        }
        var kind = definition.Kind;
        if (source.Id == target.Id)
            Block($"{name} is on {target.Name} already. Choose another endpoint.");
        if (jobs.IsBusy(source.Id, name) || jobs.IsBusy(target.Id, name))
            Block($"Something is already running on {name}. Wait for it to finish.");
        if (await store.GetAsync(target.Id, name, cancellationToken) is not null)
            Block($"There's already a stack named {name} on {target.Name}. Rename or remove one of them first.");
        try
        {
            if (kind == StackKind.Swarm)
                await docker.FindAsync(cancellationToken);
            else
                await compose.FindAsync(cancellationToken);
        }
        catch (ComposeUnavailableException ex)
        {
            Block(ex.Message);
        }
        if (source.Client is not { } sourceClient)
        {
            Block($"{source.Name} is offline, so {name} can't be stopped or read there.");
            return Plan(kind);
        }
        if (target.Client is not { } targetClient)
        {
            Block($"{target.Name} is offline.");
            return Plan(kind);
        }

        return kind == StackKind.Swarm
            ? await CheckSwarmAsync(source, sourceClient, target, name, findings, Block, Note, Plan, cancellationToken)
            : await CheckComposeAsync(source, sourceClient, target, targetClient, name, findings, Block, Warn, Note, Plan, cancellationToken);
    }

    private static async Task<MovePlan> CheckSwarmAsync(EngineConnection source, DockerClient sourceClient, EngineConnection target, string name,
        List<MoveFinding> findings, Action<string> block, Action<string> note,
        Func<StackKind, bool, IReadOnlyList<MoveVolume>?, IReadOnlyList<string>?, MovePlan> plan, CancellationToken cancellationToken)
    {
        if (target.Engine?.Has(EngineCapabilities.SwarmManager) != true)
            block($"{name} is a swarm stack, and {target.Name} isn't a swarm manager.");
        else if (source.Engine?.SwarmClusterId is { } cluster && cluster == target.Engine.SwarmClusterId)
            block($"{target.Name} manages the same swarm as {source.Name}: {name} already runs there.");

        var stack = source.Engine?.Has(EngineCapabilities.SwarmManager) == true
            ? SwarmInventory.Stacks(await sourceClient.ListServicesAsync(StackJobs.StackFilter(name), cancellationToken)).GetValueOrDefault(name)
            : null;
        var deployed = stack is { Services.Count: > 0 };
        note(deployed
            ? "Swarm volumes live on the nodes, so their data isn't copied. Copy it yourself if the services need it."
            : $"{name} isn't deployed on {source.Name}: only its files move.");
        _ = findings;
        // The nodes pull them as the services start; the digest swarm pins is left out.
        return plan(StackKind.Swarm, deployed, null,
            [.. (stack?.Services ?? []).Select(s => s.Image).OfType<string>().Select(i => i.Split('@')[0]).Distinct(StringComparer.Ordinal)]);
    }

    private async Task<MovePlan> CheckComposeAsync(EngineConnection source, DockerClient sourceClient, EngineConnection target, DockerClient targetClient,
        string name, List<MoveFinding> findings, Action<string> block, Action<string> warn, Action<string> note,
        Func<StackKind, bool, IReadOnlyList<MoveVolume>?, IReadOnlyList<string>?, MovePlan> plan, CancellationToken cancellationToken)
    {
        var project = await ProjectAsync(sourceClient, name, cancellationToken);
        if (project.IsSelf)
            block($"Sockless Port itself runs in {name}, so it can't move it.");
        if ((await ProjectAsync(targetClient, name, cancellationToken)).Containers.Count > 0)
            block($"A Compose project named {name} already runs on {target.Name}. Take it down there first.");
        if (project.Containers.Count == 0)
        {
            note($"{name} isn't deployed on {source.Name}: only its files move, and it isn't deployed on {target.Name} either.");
            return plan(StackKind.Compose, false, null, null);
        }

        var volumeNames = new SortedSet<string>(StringComparer.Ordinal);
        var binds = new SortedSet<string>(StringComparer.Ordinal);
        var images = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var container in project.Containers)
        {
            if (container.Image is { Length: > 0 } image && !image.StartsWith("sha256:", StringComparison.Ordinal))
                images.Add(image);
            var (inspect, json) = await sourceClient.InspectContainerAsync(container.Id, cancellationToken);
            foreach (var mount in inspect.Mounts ?? [])
            {
                if (mount.Type == "volume" && mount.Name is { Length: > 0 } volume)
                    volumeNames.Add(volume);
                else if (mount.Type == "bind" && mount.Source is { Length: > 0 } path)
                    binds.Add(path);
            }
            var hostConfig = JsonNode.Parse(json)?["HostConfig"];
            if (hostConfig?["Devices"] is JsonArray { Count: > 0 })
                warn($"{container.DisplayName} uses devices of {source.Name}'s host. They aren't moved: {target.Name}'s host needs the same ones.");
            if (inspect.HostConfig?.NetworkMode == "host")
                note($"{container.DisplayName} uses the host's network, so it listens on {target.Name}'s host addresses there.");
        }
        foreach (var path in binds)
            warn($"{path} on {source.Name}'s host is mounted into the stack. Bind mounts aren't copied: the path must exist on {target.Name}'s host.");

        // Host ports: the deploy fails if the target publishes them already.
        var wanted = project.Containers.SelectMany(c => c.Ports ?? []).Where(p => p.PublicPort > 0)
            .Select(p => (Port: p.PublicPort, Type: p.Type ?? "tcp")).Distinct().ToList();
        if (wanted.Count > 0)
        {
            var taken = (await targetClient.ListContainersAsync(all: false, cancellationToken))
                .SelectMany(c => (c.Ports ?? []).Where(p => p.PublicPort > 0).Select(p => (Port: p.PublicPort, Type: p.Type ?? "tcp", c.DisplayName)))
                .ToList();
            foreach (var (port, type) in wanted)
            {
                if (taken.FirstOrDefault(t => t.Port == port && t.Type == type) is { DisplayName: { } user })
                    block($"Port {port}/{type} is already published on {target.Name} by {user}. Free it first, or the deploy fails.");
            }
            if (target.Engine?.Has(EngineCapabilities.Rootless) == true && wanted.Any(w => w.Port < 1024))
                warn($"{target.Name} runs rootless, which usually can't publish ports below 1024. The deploy fails unless its host allows it.");
        }

        // Images: a copied image runs only on the architecture it was built for.
        var targetArchitecture = Architecture(target.Engine?.Architecture);
        foreach (var image in images)
        {
            try
            {
                var (inspect, _) = await sourceClient.InspectImageAsync(image, cancellationToken);
                if (Architecture(inspect.Architecture) is { } built && targetArchitecture is not null && built != targetArchitecture)
                    warn($"{image} on {source.Name} is built for {built}, and {target.Name} runs {targetArchitecture}. It runs there only if its registry has a {targetArchitecture} variant.");
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
            }
        }

        var found = new List<MoveVolume>();
        foreach (var volume in volumeNames)
            found.Add(new MoveVolume(volume, await VolumeProblemAsync(sourceClient, target, targetClient, volume, cancellationToken)));
        if (found.Count > 0 && found.All(v => !v.Copyable))
            note("None of its volumes can be copied: the stack starts on the target with what's there.");
        _ = findings;
        return plan(StackKind.Compose, true, found, [.. images]);
    }

    /// <summary>Why a volume's data can't be copied to the target, or null when it can.</summary>
    private static async Task<string?> VolumeProblemAsync(DockerClient sourceClient, EngineConnection target, DockerClient targetClient, string name,
        CancellationToken cancellationToken)
    {
        var (volume, _) = await sourceClient.InspectVolumeAsync(name, cancellationToken);
        if (volume.Driver is { Length: > 0 } driver && driver != "local")
            return $"It uses the {driver} driver, which stores the data outside the host: it isn't copied.";
        if (volume.Options?.ContainsKey("device") == true)
            return $"It's bound to {volume.Options["device"]} on the host: it isn't copied, and that path must exist on {target.Name}'s host.";
        try
        {
            await targetClient.InspectVolumeAsync(name, cancellationToken);
            return $"A volume named {name} exists on {target.Name} already: the stack uses it as it is, and nothing is copied into it.";
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>"amd64" for "x86_64", "arm64" for "aarch64": the names engines and images use differ.</summary>
    internal static string? Architecture(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => null,
        "x86_64" => "amd64",
        "aarch64" => "arm64",
        var other => other,
    };

    /// <summary>
    /// Checks again, then starts the move as a job of the target's stack.
    /// </summary>
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
                        await CopyVolumeAsync(job, source, target, volume.Name, token);
                    }
                    catch (Exception ex) when (IsEngineFailure(ex, token))
                    {
                        return await UndoAsync(job, source, target, plan, stopped, $"Couldn't copy the volume {volume.Name}: {EngineErrors.Sentence(ex.Message)}");
                    }
                }
            }

            // Swarm nodes pull their own images (with the registry credentials the deploy passes).
            if (plan.Kind == StackKind.Compose)
            {
                foreach (var image in plan.Images)
                    await EnsureImageAsync(job, source, target, image, token);
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
                return new StackJobSucceeded($"Moved {name} to {target.Name}. {moved[(name.Length + 1)..].TrimEnd('.')}, and it's removed from {source.Name}; its volumes are still there.");

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

    /// <summary>
    /// Creates the volume on the target as it is on the source (driver, options, labels, so Compose
    /// takes it as its own), then streams its contents from a read-only helper on the source to a
    /// writable one on the target, through the server.
    /// </summary>
    private async Task CopyVolumeAsync(StackJob job, EngineConnection source, EngineConnection target, string name, CancellationToken cancellationToken)
    {
        var sourceClient = source.Client ?? throw new IOException($"{source.Name} went offline.");
        var targetClient = target.Client ?? throw new IOException($"{target.Name} went offline.");
        var (volume, _) = await sourceClient.InspectVolumeAsync(name, cancellationToken);
        job.AppendLine($"Copying the volume {name}…");
        await targetClient.CreateVolumeAsync(new VolumeCreateRequest
        {
            Name = name,
            Driver = volume.Driver ?? "local",
            DriverOpts = volume.Options,
            Labels = volume.Labels,
        }, cancellationToken);

        string? reader = null, writer = null;
        try
        {
            reader = await volumes.StartCopyHelperAsync(sourceClient, name, readOnly: true, cancellationToken);
            writer = await volumes.StartCopyHelperAsync(targetClient, name, readOnly: false, cancellationToken);
            await using var archive = await sourceClient.GetArchiveAsync(reader, VolumeBrowser.MountPath, cancellationToken);
            var next = ProgressEvery;
            await using var counting = new CountingStream(archive, copied =>
            {
                if (copied < next)
                    return;
                next = copied + ProgressEvery;
                job.AppendLine($"  {Bytes(copied)} of {name} copied…");
            });
            // The archive's entries are "volume/…": unpacked at the root, they land in the target's mount.
            await targetClient.PutArchiveAsync(writer, "/", counting, cancellationToken, Timeout.InfiniteTimeSpan);
            job.AppendLine($"Copied the volume {name} ({Bytes(counting.Count)}).");
        }
        catch (VolumeBrowseException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            if (reader is not null)
                await volumes.RemoveCopyHelperAsync(sourceClient, reader);
            if (writer is not null)
                await volumes.RemoveCopyHelperAsync(targetClient, writer);
        }
    }

    /// <summary>
    /// Makes sure the target has the image before the deploy: it's there, or it's pulled (with the
    /// registry's credentials), or else it's copied from the source, for images that are in no
    /// registry the target can reach. A failure is only logged: the deploy says what's missing.
    /// </summary>
    private async Task EnsureImageAsync(StackJob job, EngineConnection source, EngineConnection target, string image, CancellationToken cancellationToken)
    {
        var targetClient = target.Client ?? throw new IOException($"{target.Name} went offline.");
        try
        {
            if (await targetClient.ImageExistsAsync(image, cancellationToken))
            {
                job.AppendLine($"{image} is on {target.Name} already.");
                return;
            }
            job.AppendLine($"Pulling {image} on {target.Name}…");
            var auth = await registries.AuthHeaderForAsync(image, cancellationToken);
            await foreach (var _ in targetClient.PullImageAsync(image, auth, cancellationToken)) { }
            job.AppendLine($"Pulled {image}.");
            return;
        }
        catch (Exception ex) when (IsEngineFailure(ex, cancellationToken))
        {
            job.AppendLine($"Couldn't pull {image} on {target.Name}: {EngineErrors.Sentence(ex.Message)} Copying it from {source.Name} instead.");
        }

        var sourceClient = source.Client ?? throw new IOException($"{source.Name} went offline.");
        try
        {
            await using var tar = await sourceClient.ExportImageAsync(image, cancellationToken: cancellationToken);
            await using var counting = new CountingStream(tar, _ => { });
            var loaded = await targetClient.LoadImageAsync(counting, cancellationToken);
            job.AppendLine($"Copied {image} from {source.Name} ({Bytes(counting.Count)}).{(loaded.Count > 0 ? $" {string.Join(" ", loaded)}" : "")}");
        }
        catch (Exception ex) when (IsEngineFailure(ex, cancellationToken))
        {
            job.AppendLine($"Couldn't copy {image} either: {EngineErrors.Sentence(ex.Message)}");
        }
    }

    private static bool IsEngineFailure(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is DockerApiException or HttpRequestException or IOException or TimeoutException or TaskCanceledException or ObjectDisposedException
            or InvalidDataException or System.Text.Json.JsonException;

    private static async Task<ComposeProject> ProjectAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        var containers = await client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{ContainerLabels.ComposeProject}={name}"] }, all: true, cancellationToken);
        return StackInventory.Projects(containers).GetValueOrDefault(name) ?? ComposeProject.Empty(name);
    }

    /// <summary>"1.2 GB": decimal units, as the engine's own sizes.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
