using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Stacks;

/// <summary>A stack job couldn't start. The message is for people.</summary>
public sealed class StackJobRejectedException(string message) : Exception(message);

/// <param name="RemoveVolumes">For down and remove: also delete the stack's volumes.</param>
/// <param name="Down">For remove: take the stack down first, rather than leaving its containers running.</param>
public sealed record StackJobOptions(bool RemoveVolumes = false, bool Down = true);

/// <summary>
/// Runs Compose on stacks in the background, one job per stack at a time, and keeps finished jobs
/// for a while so the stack's page can show how the last one ended.
/// </summary>
public sealed class StackJobs(ComposeCli compose, StackStore store, TimeProvider time, ILogger<StackJobs> logger) : IDisposable
{
    public const int MaxRunningPerEndpoint = 4;

    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

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
    /// files; any other Compose project on the engine is addressed by name (start, stop, restart, down).
    /// </summary>
    /// <exception cref="StackJobRejectedException">Offline, Compose missing, busy, or it's Sockless Port's own stack.</exception>
    public async Task<StackJob> StartAsync(EngineConnection connection, string stackName, StackOperation operation, StackJobOptions? options = null)
    {
        options ??= new StackJobOptions();
        var client = connection.Client ?? throw new StackJobRejectedException("The engine is offline, so nothing was started.");
        try
        {
            await compose.FindAsync(_stopping.Token);
        }
        catch (ComposeUnavailableException ex)
        {
            throw new StackJobRejectedException(ex.Message);
        }

        var definition = await store.GetAsync(connection.Id, stackName, _stopping.Token);
        var managed = definition is not null && store.Files.Exists(connection.Id, stackName);
        if (!managed && operation is StackOperation.Deploy or StackOperation.Update or StackOperation.Remove)
        {
            throw new StackJobRejectedException(definition is null
                ? $"Sockless Port doesn't manage a stack named {stackName}. Adopt it first."
                : $"The files of {stackName} are missing from {store.Files.Directory(connection.Id, stackName)}.");
        }

        if (operation is not (StackOperation.Remove) || options.Down)
        {
            var project = await ProjectAsync(client, stackName, _stopping.Token);
            if (project.IsSelf)
                throw new StackJobRejectedException($"Sockless Port itself runs in {stackName}, so it can't {Verb(operation)} it from here. Use docker compose on the host.");
        }

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
        _ = RunAsync(job, connection, directory, options);
        return job;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>What a job runs, in order. Exposed for tests.</summary>
    internal static IReadOnlyList<string[]> Steps(StackOperation operation, StackJobOptions options) => operation switch
    {
        StackOperation.Deploy => [["up", "--detach", "--remove-orphans"]],
        StackOperation.Update => [["up", "--detach", "--remove-orphans", "--pull", "always"]],
        StackOperation.Start => [["start"]],
        StackOperation.Stop => [["stop"]],
        StackOperation.Restart => [["restart"]],
        StackOperation.Down => [Down(options)],
        StackOperation.Remove => options.Down ? [Down(options)] : [],
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static string[] Down(StackJobOptions options) =>
        options.RemoveVolumes ? ["down", "--remove-orphans", "--volumes"] : ["down", "--remove-orphans"];

    private async Task RunAsync(StackJob job, EngineConnection connection, string? directory, StackJobOptions options)
    {
        await Task.Yield();
        StackJobStatus outcome;
        try
        {
            outcome = await RunStepsAsync(job, connection, directory, options);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.AppendLine("Cancelled. What Compose already did stays done.");
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
    }

    private async Task<StackJobStatus> RunStepsAsync(StackJob job, EngineConnection connection, string? directory, StackJobOptions options)
    {
        var name = job.StackName;
        // What's deployed is what Compose read at the start, not what someone saves meanwhile.
        var content = directory is null ? null : await store.Files.ReadAsync(connection.Id, name, job.Token);
        var tool = await compose.FindAsync(job.Token);

        foreach (var step in Steps(job.Operation, options))
        {
            job.AppendLine($"$ {tool.DisplayName} {string.Join(' ', step)}");
            var result = await compose.RunAsync(new ComposeRequest(name, step, connection.Address, directory), job.AppendLine, job.Token);
            if (!result.Succeeded)
                return Failure(step[0], result);
        }

        switch (job.Operation)
        {
            case StackOperation.Deploy or StackOperation.Update when content is not null:
                await store.MarkDeployedAsync(connection.Id, name, content, CancellationToken.None);
                break;
            case StackOperation.Down when directory is not null:
                await store.MarkDownAsync(connection.Id, name, CancellationToken.None);
                break;
            case StackOperation.Remove:
                await store.DeleteAsync(connection.Id, name, CancellationToken.None);
                job.AppendLine($"Deleted the files of {name}.");
                break;
        }

        return new StackJobSucceeded(job.Operation switch
        {
            StackOperation.Deploy => $"Deployed {name}.",
            StackOperation.Update => $"Pulled the images of {name} and recreated what changed.",
            StackOperation.Start => $"Started {name}.",
            StackOperation.Stop => $"Stopped {name}.",
            StackOperation.Restart => $"Restarted {name}.",
            StackOperation.Down => options.RemoveVolumes ? $"Took {name} down and removed its volumes." : $"Took {name} down. Its volumes are kept.",
            StackOperation.Remove => !options.Down ? $"Removed the stack {name}. Its containers were left running."
                : options.RemoveVolumes ? $"Took {name} down, removed its volumes and deleted the stack."
                : $"Took {name} down and deleted the stack. Its volumes are kept.",
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Operation, null),
        });
    }

    /// <summary>The line that says what went wrong (Compose ends with it), and hints for known problems.</summary>
    internal static StackJobFailed Failure(string command, ComposeResult result)
    {
        var reason = result.Output.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                     ?? result.Output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        var error = reason is null
            ? $"docker compose {command} failed with exit code {result.ExitCode}."
            : $"docker compose {command} failed: {EngineErrors.Sentence(TrimErrorPrefix(reason))}";
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
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

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
