using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;

namespace Sockless.Port.Stacks;

/// <summary>A stack job couldn't start. The message is for people.</summary>
public sealed class StackJobRejectedException(string message) : Exception(message);

/// <param name="RemoveVolumes">For down and remove: also delete the stack's volumes.</param>
/// <param name="Down">For remove: take the stack down first, rather than leaving its containers running.</param>
/// <param name="Automatic">For sync: started by a poll or a webhook, so it only deploys a stack that's deployed, and only when its files changed.</param>
public sealed record StackJobOptions(bool RemoveVolumes = false, bool Down = true, bool Automatic = false);

/// <summary>
/// Runs Compose (or, for swarm stacks, <c>docker stack</c>) on stacks in the background, one job
/// per stack at a time, and keeps finished jobs for a while so the stack's page can show how the
/// last one ended.
/// </summary>
public sealed class StackJobs(ComposeCli compose, DockerCli docker, StackStore store, SwarmServices services, StackGit git, TimeProvider time,
    ILogger<StackJobs> logger, Notifications.Alerts? alerts = null) : IDisposable
{
    public const int MaxRunningPerEndpoint = 4;

    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    /// <summary>What a swarm stack can do. Swarm has no stopped state: scale services to 0 or take the stack down.</summary>
    public static readonly IReadOnlySet<StackOperation> SwarmOperations = new HashSet<StackOperation>
    {
        StackOperation.Deploy, StackOperation.Update, StackOperation.Restart, StackOperation.Down, StackOperation.Remove,
        StackOperation.Fetch, StackOperation.Sync,
    };

    private readonly ConcurrentDictionary<string, StackJob> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _startGate = new();
    private readonly CancellationTokenSource _stopping = new();

    public StackJob? Get(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>The stack's running job, or else its most recent one that's still kept.</summary>
    public StackJob? Latest(int endpointId, string stackName)
    {
        Prune();
        return _jobs.Values
            .Where(j => j.EndpointId == endpointId && j.StackName == stackName)
            .OrderByDescending(j => j.IsRunning)
            .ThenByDescending(j => j.StartedAt)
            .FirstOrDefault();
    }

    public bool IsBusy(int endpointId, string stackName) =>
        _jobs.Values.Any(j => j.IsRunning && j.EndpointId == endpointId && j.StackName == stackName);

    /// <summary>
    /// Starts <paramref name="operation"/> on the stack. A stack Sockless Port manages runs from its
    /// files; any other project on the engine is addressed by name (start, stop, restart, down).
    /// A git-backed stack can also fetch (which needs no engine) and sync.
    /// </summary>
    /// <exception cref="StackJobRejectedException">Offline, the tool missing, busy, not possible for the kind, or it's Sockless Port's own stack.</exception>
    public async Task<StackJob> StartAsync(EngineConnection connection, string stackName, StackOperation operation, StackJobOptions? options = null)
    {
        options ??= new StackJobOptions();
        var fetchOnly = operation == StackOperation.Fetch;
        var client = connection.Client;
        if (client is null && !fetchOnly)
            throw new StackJobRejectedException("The engine is offline, so nothing was started.");

        var definition = await store.GetAsync(connection.Id, stackName, _stopping.Token);
        var managed = definition is not null && store.Files.Exists(connection.Id, stackName);
        if (!managed && operation is StackOperation.Deploy or StackOperation.Update or StackOperation.Remove or StackOperation.Fetch or StackOperation.Sync)
        {
            throw new StackJobRejectedException(definition is null
                ? $"Sockless Port doesn't manage a stack named {stackName}. Adopt it first."
                : $"The files of {stackName} are missing from {store.Files.Directory(connection.Id, stackName)}.");
        }

        var repository = definition is null ? null : await store.GetRepositoryAsync(definition.Id, _stopping.Token);
        if (repository is null && operation is StackOperation.Fetch or StackOperation.Sync)
            throw new StackJobRejectedException($"{stackName} doesn't come from a git repository.");
        if (repository is not null && operation is StackOperation.Deploy or StackOperation.Update or StackOperation.Fetch or StackOperation.Sync)
        {
            try
            {
                await git.Cli.FindAsync(_stopping.Token);
            }
            catch (ComposeUnavailableException ex)
            {
                throw new StackJobRejectedException(ex.Message);
            }
        }

        var kind = definition?.Kind ?? await ExternalKindAsync(connection, client!, stackName, _stopping.Token);
        if (!fetchOnly)
            await CheckDeployableAsync(connection, client!, stackName, kind, operation, options);

        StackJob job;
        lock (_startGate)
        {
            Prune();
            if (IsBusy(connection.Id, stackName))
                throw new StackJobRejectedException($"Something is already running on {stackName}. Wait for it to finish.");
            if (_jobs.Values.Count(j => j.IsRunning && j.EndpointId == connection.Id) >= MaxRunningPerEndpoint)
                throw new StackJobRejectedException($"{MaxRunningPerEndpoint} stack jobs are already running on {connection.Name}. Wait for one to finish.");
            job = new StackJob(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), connection.Id, stackName, operation, time.GetUtcNow(), _stopping.Token);
            _jobs[job.Id] = job;
        }

        var directory = managed ? store.Files.Directory(connection.Id, stackName) : null;
        _ = RunAsync(job, connection, kind, directory, options);
        return job;
    }

    /// <summary>The tool is there, a swarm stack is reached through a manager, and it isn't the stack Sockless Port runs in.</summary>
    private async Task CheckDeployableAsync(EngineConnection connection, DockerClient client, string stackName, StackKind kind, StackOperation operation,
        StackJobOptions options)
    {
        try
        {
            if (kind == StackKind.Swarm)
                await docker.FindAsync(_stopping.Token);
            else
                await compose.FindAsync(_stopping.Token);
        }
        catch (ComposeUnavailableException ex)
        {
            throw new StackJobRejectedException(ex.Message);
        }

        if (kind == StackKind.Swarm)
        {
            if (connection.Engine?.Has(EngineCapabilities.SwarmManager) != true)
                throw new StackJobRejectedException($"{stackName} is a swarm stack, and {connection.Name} isn't a swarm manager. Connect to a manager to deploy or change it.");
            if (!SwarmOperations.Contains(operation))
                throw new StackJobRejectedException($"Swarm stacks can't {Verb(operation)}: scale their services to 0, or take the stack down.");
        }

        if (operation is not (StackOperation.Remove) || options.Down)
        {
            var self = kind == StackKind.Swarm
                ? SwarmInventory.IncludesSelf(await SwarmTasksAsync(client, stackName, _stopping.Token))
                : (await ProjectAsync(client, stackName, _stopping.Token)).IsSelf;
            if (self)
                throw new StackJobRejectedException($"Sockless Port itself runs in {stackName}, so it can't {Verb(operation)} it from here. Use the Docker CLI on the host.");
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>What a Compose job runs, in order. Exposed for tests.</summary>
    internal static IReadOnlyList<string[]> Steps(StackOperation operation, StackJobOptions options) => operation switch
    {
        StackOperation.Deploy or StackOperation.Sync => [["up", "--detach", "--remove-orphans"]],
        StackOperation.Update => [["up", "--detach", "--remove-orphans", "--pull", "always"]],
        StackOperation.Fetch => [],
        StackOperation.Start => [["start"]],
        StackOperation.Stop => [["stop"]],
        StackOperation.Restart => [["restart"]],
        StackOperation.Down => [Down(options)],
        StackOperation.Remove => options.Down ? [Down(options)] : [],
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>
    /// What a swarm job runs with the Docker CLI. Deploy resolves images only where they changed;
    /// update asks the registry again for every one. Restart has no CLI step: it force-updates
    /// each service through the API. Exposed for tests.
    /// </summary>
    internal static IReadOnlyList<string[]> SwarmSteps(StackOperation operation, string name, string? composeFile, StackJobOptions options) => operation switch
    {
        StackOperation.Deploy or StackOperation.Update or StackOperation.Sync when composeFile is null => throw new ArgumentNullException(nameof(composeFile)),
        StackOperation.Deploy or StackOperation.Sync => [Deploy(name, composeFile, "changed")],
        StackOperation.Update => [Deploy(name, composeFile, "always")],
        StackOperation.Fetch => [],
        StackOperation.Restart => [],
        StackOperation.Down => [["stack", "rm", name]],
        StackOperation.Remove => options.Down ? [["stack", "rm", name]] : [],
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Swarm stacks don't start or stop."),
    };

    private static string[] Deploy(string name, string composeFile, string resolve) =>
        ["stack", "deploy", "--compose-file", composeFile, "--with-registry-auth", "--prune", "--detach=true", "--resolve-image", resolve, name];

    private static string[] Down(StackJobOptions options) =>
        options.RemoveVolumes ? ["down", "--remove-orphans", "--volumes"] : ["down", "--remove-orphans"];

    private async Task RunAsync(StackJob job, EngineConnection connection, StackKind kind, string? directory, StackJobOptions options)
    {
        await Task.Yield();
        StackJobStatus outcome;
        try
        {
            outcome = await RunStepsAsync(job, connection, kind, directory, options);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.AppendLine(kind == StackKind.Swarm ? "Cancelled. What was already sent to the swarm stays done." : "Cancelled. What Compose already did stays done.");
            outcome = new StackJobCanceled();
        }
        catch (ComposeUnavailableException ex)
        {
            outcome = new StackJobFailed(ex.Message, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "{Operation} of stack {Stack} on endpoint {Endpoint} failed", job.Operation, job.StackName, job.EndpointId);
            outcome = new StackJobFailed(EngineErrors.Sentence(ex.Message), []);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} of stack {Stack} on endpoint {Endpoint} failed unexpectedly", job.Operation, job.StackName, job.EndpointId);
            outcome = new StackJobFailed("Something went wrong on the server. The log has the details.", []);
        }
        job.Finish(outcome, time.GetUtcNow());
        if (outcome is StackJobFailed failed && alerts is not null)
        {
            try
            {
                await alerts.TaskFailedAsync(connection, $"{job.Operation} of the stack {job.StackName} failed on {connection.Name}",
                    failed.Hints.Count == 0 ? failed.Error : $"{failed.Error}\n{string.Join('\n', failed.Hints)}",
                    $"/endpoints/{connection.Id}/stacks/{Uri.EscapeDataString(job.StackName)}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Couldn't raise an alert for the failed job of {Stack}", job.StackName);
            }
        }
    }

    private async Task<StackJobStatus> RunStepsAsync(StackJob job, EngineConnection connection, StackKind kind, string? directory, StackJobOptions options)
    {
        var name = job.StackName;
        var definition = directory is null ? null : await store.GetAsync(connection.Id, name, job.Token);
        var repository = definition is null ? null : await store.GetRepositoryAsync(definition.Id, job.Token);
        StackSource? source = null;
        if (repository is not null)
        {
            var (done, current) = await PrepareCheckoutAsync(job, connection, definition!, repository, options);
            if (done is not null)
                return done;
            repository = current;
            if (job.Operation is StackOperation.Deploy or StackOperation.Update or StackOperation.Sync)
            {
                source = store.Source(connection.Id, name, current);
                if (!File.Exists(source.ComposeFile))
                    return new StackJobFailed($"{current.ComposePath} isn't in the checkout of {name}. Fetch, so the files and the repository settings agree, then deploy.", []);
            }
        }

        // What's deployed is what was read at the start, not what someone saves meanwhile.
        var content = directory is null ? null : await store.Files.ReadAsync(connection.Id, name, job.Token);
        if (source is not null)
            job.AppendLine($"Deploying {GitSource.Short(repository!.FetchedCommit)} ({repository.FetchedSubject}) from {repository.Branch}.");

        if (kind == StackKind.Swarm)
        {
            if (await RunSwarmAsync(job, connection, directory, source, content, options) is { } failed)
                return failed;
        }
        else
        {
            var tool = await compose.FindAsync(job.Token);
            foreach (var step in Steps(job.Operation, options))
            {
                job.AppendLine($"$ {tool.DisplayName} {string.Join(' ', step)}");
                var result = await compose.RunAsync(new ComposeRequest(name, step, connection.Address, directory, source), job.AppendLine, job.Token);
                if (!result.Succeeded)
                    return Failure($"docker compose {step[0]}", result);
            }
        }

        switch (job.Operation)
        {
            case StackOperation.Deploy or StackOperation.Update or StackOperation.Sync when content is not null:
                await store.MarkDeployedAsync(connection.Id, name, content, CancellationToken.None,
                    source is null ? null : repository!.FetchedCommit, source is null ? null : repository!.FetchedSubject);
                break;
            case StackOperation.Down when directory is not null:
                await store.MarkDownAsync(connection.Id, name, CancellationToken.None);
                break;
            case StackOperation.Remove:
                await store.DeleteAsync(connection.Id, name, CancellationToken.None);
                job.AppendLine($"Deleted the files of {name}.");
                break;
        }

        var swarm = kind == StackKind.Swarm;
        var at = source is null ? "" : $" at {GitSource.Short(repository!.FetchedCommit)}";
        return new StackJobSucceeded(job.Operation switch
        {
            StackOperation.Deploy or StackOperation.Sync => swarm ? $"Deployed {name}{at}. Its services now converge on the nodes." : $"Deployed {name}{at}.",
            StackOperation.Update => swarm ? $"Deployed {name}, with every image resolved again." : $"Pulled the images of {name} and recreated what changed.",
            StackOperation.Start => $"Started {name}.",
            StackOperation.Stop => $"Stopped {name}.",
            StackOperation.Restart => swarm ? $"Replacing the tasks of every service of {name}." : $"Restarted {name}.",
            StackOperation.Down => swarm ? $"Removed the services and networks of {name} from the swarm. Volumes stay on the nodes."
                : options.RemoveVolumes ? $"Took {name} down and removed its volumes." : $"Took {name} down. Its volumes are kept.",
            StackOperation.Remove => !options.Down ? $"Removed the stack {name}. {(swarm ? "Its services" : "Its containers")} were left running."
                : swarm ? $"Removed {name} from the swarm and deleted the stack. Volumes stay on the nodes."
                : options.RemoveVolumes ? $"Took {name} down, removed its volumes and deleted the stack."
                : $"Took {name} down and deleted the stack. Its volumes are kept.",
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Operation, null),
        });
    }

    /// <summary>
    /// The git part of a git-backed stack's job. Fetch and sync take the branch's newest commit;
    /// a deploy whose checkout is gone (after a restore) checks out the commit it was fetched at
    /// again, so what's deployed is still what was reviewed.
    /// </summary>
    /// <returns>
    /// <c>Done</c>: the job's outcome when it ends here (a fetch, a failure, an automatic sync with
    /// nothing to do). Otherwise the repository as it is now, to deploy from.
    /// </returns>
    private async Task<(StackJobStatus? Done, StackRepository Repository)> PrepareCheckoutAsync(StackJob job, EngineConnection connection,
        StackDefinition definition, StackRepository repository, StackJobOptions options)
    {
        var name = job.StackName;
        var checkout = store.CheckoutDirectory(connection.Id, name);
        var fetching = job.Operation is StackOperation.Fetch or StackOperation.Sync;
        var rebuild = job.Operation is StackOperation.Deploy or StackOperation.Update && !Directory.Exists(Path.Combine(checkout, ".git"));
        if (!fetching && !rebuild)
            return (null, repository);

        if (rebuild)
            job.AppendLine($"The checkout of {name} is missing: checking out {GitSource.Short(repository.FetchedCommit)} again.");
        try
        {
            var fetched = await git.FetchAsync(store.CheckoutRequest(repository), checkout, rebuild ? repository.FetchedCommit : null, job.AppendLine, job.Token);
            if (fetching)
                await store.RecordFetchAsync(connection.Id, name, fetched, CancellationToken.None);
        }
        catch (GitException ex)
        {
            job.AppendLine(ex.Message);
            await store.RecordCheckAsync(definition.Id, ex.Message, cancellationToken: CancellationToken.None);
            return (new StackJobFailed(ex.Message, []), repository);
        }

        var current = await store.GetRepositoryAsync(definition.Id, CancellationToken.None) ?? repository;
        if (!fetching)
            return (null, current);

        var saved = await store.Files.ReadAsync(connection.Id, name, CancellationToken.None);
        var deployed = await store.Files.ReadDeployedAsync(connection.Id, name, CancellationToken.None);
        var commit = $"{GitSource.Short(current.FetchedCommit)} ({current.FetchedSubject})";
        var sameAsDeployed = deployed is not null && saved == deployed;
        StackJobStatus? done = null;
        if (job.Operation == StackOperation.Fetch)
        {
            done = new StackJobSucceeded(definition.DeployedAt is null ? $"Fetched {commit}. Deploy {name} to run it."
                : sameAsDeployed ? $"Fetched {commit}. The files are the same as what's deployed."
                : $"Fetched {commit}. Review the changes, then deploy them.");
        }
        else if (options.Automatic && definition.DeployedAt is null)
            done = new StackJobSucceeded($"Fetched {commit}. {name} isn't deployed, so it wasn't deployed now either.");
        else if (options.Automatic && sameAsDeployed)
            done = new StackJobSucceeded($"Fetched {commit}. The files are the same as what's deployed, so nothing was redeployed.");
        return (done, current);
    }

    /// <returns>The failure, or null when every step succeeded.</returns>
    private async Task<StackJobFailed?> RunSwarmAsync(StackJob job, EngineConnection connection, string? directory, StackSource? source, StackContent? content,
        StackJobOptions options)
    {
        var name = job.StackName;
        if (job.Operation == StackOperation.Restart)
            return await RestartServicesAsync(job, connection, name);

        var tool = await docker.FindAsync(job.Token);
        // A git stack runs its checkout's file: docker stack resolves relative paths against the file's folder.
        var file = source?.ComposeFile ?? (directory is null ? null : Path.Combine(directory, StackFiles.ComposeFile));
        directory = source?.Directory ?? directory;
        var variables = StackEnv.Parse(content?.Env);
        foreach (var step in SwarmSteps(job.Operation, name, file, options))
        {
            job.AppendLine($"$ {tool.FileName} {string.Join(' ', step.Select(a => a == file ? StackFiles.ComposeFile : a))}");
            var result = await docker.RunAsync(new DockerCliRequest(step, connection.Address, directory, variables), job.AppendLine, job.Token);
            if (!result.Succeeded)
                return Failure($"docker {step[0]} {step[1]}", result);
        }
        return null;
    }

    private async Task<StackJobFailed?> RestartServicesAsync(StackJob job, EngineConnection connection, string name)
    {
        var client = connection.Client ?? throw new IOException("The engine went offline.");
        var stackServices = await client.ListServicesAsync(StackFilter(name), job.Token);
        if (stackServices.Count == 0)
            return new StackJobFailed($"{name} has no services on the swarm. Deploy it first.", []);
        foreach (var service in stackServices.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            job.AppendLine($"Replacing the tasks of {service.Name}…");
            try
            {
                await services.RedeployAsync(client, service.Id, pull: false, job.Token);
            }
            catch (DockerApiException ex)
            {
                job.AppendLine($"Error: {ex.Message}");
                return new StackJobFailed($"Couldn't restart {service.Name}: {EngineErrors.Sentence(ex.Message)}", []);
            }
        }
        job.AppendLine("Each service replaces its tasks as its update config says.");
        return null;
    }

    /// <summary>The line that says what went wrong (the tool ends with it), and hints for known problems.</summary>
    internal static StackJobFailed Failure(string command, ComposeResult result)
    {
        var reason = result.Output.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                     ?? result.Output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        var error = reason is null
            ? $"{command} failed with exit code {result.ExitCode}."
            : $"{command} failed: {EngineErrors.Sentence(TrimErrorPrefix(reason))}";
        return new StackJobFailed(error, ComposeHints.FromOutput(result.Output));
    }

    private static string TrimErrorPrefix(string line)
    {
        line = line.Trim();
        foreach (var prefix in (string[])["Error response from daemon: ", "Error: ", "error: "])
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..];
        }
        return line;
    }

    private static string Verb(StackOperation operation) => operation switch
    {
        StackOperation.Deploy => "deploy",
        StackOperation.Update => "update",
        StackOperation.Start => "start",
        StackOperation.Stop => "stop",
        StackOperation.Restart => "restart",
        StackOperation.Down => "take down",
        StackOperation.Remove => "remove",
        StackOperation.Fetch => "fetch",
        StackOperation.Sync => "fetch and deploy",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    internal static Dictionary<string, string[]> StackFilter(string name) => new() { ["label"] = [$"{ServiceSpec.StackNamespaceLabel}={name}"] };

    /// <summary>For a stack Sockless Port doesn't manage: a Compose project if it has containers, else a swarm stack if it has services.</summary>
    private static async Task<StackKind> ExternalKindAsync(EngineConnection connection, DockerClient client, string name, CancellationToken cancellationToken)
    {
        if ((await ProjectAsync(client, name, cancellationToken)).Containers.Count > 0 || connection.Engine?.Has(EngineCapabilities.SwarmManager) != true)
            return StackKind.Compose;
        try
        {
            return (await client.ListServicesAsync(StackFilter(name), cancellationToken)).Count > 0 ? StackKind.Swarm : StackKind.Compose;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw new StackJobRejectedException($"Couldn't list the services of {name}: {ex.Message}");
        }
    }

    private static async Task<ComposeProject> ProjectAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            var containers = await client.ListContainersAsync(
                new Dictionary<string, string[]> { ["label"] = [$"{Containers.ContainerLabels.ComposeProject}={name}"] }, all: true, cancellationToken);
            return StackInventory.Projects(containers).GetValueOrDefault(name) ?? ComposeProject.Empty(name);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw new StackJobRejectedException($"Couldn't list the containers of {name}: {ex.Message}");
        }
    }

    /// <summary>The current tasks of a swarm stack's services.</summary>
    private static async Task<IReadOnlyList<SwarmTask>> SwarmTasksAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            var ids = (await client.ListServicesAsync(StackFilter(name), cancellationToken)).Select(s => s.Id).ToArray();
            return ids.Length == 0 ? [] : await client.ListTasksAsync(new Dictionary<string, string[]> { ["service"] = ids, ["desired-state"] = ["running"] }, cancellationToken);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw new StackJobRejectedException($"Couldn't list the services of {name}: {ex.Message}");
        }
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } finished && finished < cutoff)
                _jobs.TryRemove(id, out _);
        }
    }
}
