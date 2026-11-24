using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Audit;
using Sockless.Port.Engines;

namespace Sockless.Port.Stacks;

/// <summary>A sync a webhook (or a poll) asked for, waiting until the stack is free and its engine online.</summary>
/// <param name="By">Who asked, for the audit log: "webhook (GitHub)", "poll".</param>
public sealed record PendingSync(int StackId, string By, DateTimeOffset RequestedAt, string? LastRejection = null);

/// <summary>
/// Redeploys git-backed stacks when their branch moves: a webhook call queues a sync, and every
/// polled stack whose interval has passed is checked with <c>git ls-remote</c> (no fetch), queueing
/// a sync when the branch is at a commit that wasn't fetched yet. Queued syncs start once nothing
/// else runs on the stack and its engine is online; they give up after an hour. Syncs started here
/// are automatic: they deploy only a deployed stack whose files changed. Each is audited when it
/// ends, with the commit.
/// </summary>
public sealed partial class GitStackTriggers(StackStore store, StackJobs jobs, StackGit git, EngineConnections connections, AuditLog audit,
    TimeProvider time, ILogger<GitStackTriggers> logger, Notifications.Alerts? alerts = null) : BackgroundService
{
    /// <summary>The poll intervals offered, in minutes.</summary>
    public static readonly IReadOnlyList<int> PollChoices = [5, 15, 60, 360, 1440];

    /// <summary>How long a queued sync waits for its stack before it's dropped.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);

    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<int, PendingSync> _pending = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Queues a sync of the stack; one queued per stack, whoever asks again.</summary>
    public void Request(int stackId, string by)
    {
        _pending.TryAdd(stackId, new PendingSync(stackId, by, time.GetUtcNow()));
        // Start it now rather than at the next tick, when nothing stands in the way.
        _ = Task.Run(() => RunPendingAsync(CancellationToken.None));
    }

    /// <summary>The queued sync of a stack, if one waits.</summary>
    public PendingSync? Pending(int stackId) => _pending.GetValueOrDefault(stackId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                try
                {
                    await RunPendingAsync(stoppingToken);
                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Checking git-backed stacks failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Starts the queued syncs that can start.</summary>
    internal async Task RunPendingAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            return;
        try
        {
            foreach (var pending in _pending.Values)
                await TryStartAsync(pending, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Checks the polled stacks whose interval has passed.</summary>
    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var (stack, repository) in await store.AllRepositoriesAsync(cancellationToken))
        {
            if (repository.PollMinutes is not { } minutes || (repository.CheckedAt is { } checkedAt && now - checkedAt < TimeSpan.FromMinutes(minutes))
                || stack.DeployedAt is null || _pending.ContainsKey(stack.Id) || jobs.IsBusy(stack.EndpointId, stack.Name)
                || connections.Get(stack.EndpointId) is not { Client: not null } connection)
                continue;

            string commit;
            try
            {
                string? knownHosts;
                (commit, knownHosts) = await git.HeadAsync(store.Remote(repository), repository.Branch, cancellationToken);
                await store.RecordCheckAsync(stack.Id, null, knownHosts, cancellationToken);
            }
            catch (Exception ex) when (ex is GitException or ComposeUnavailableException or IOException)
            {
                LogCheckFailed(logger, stack.Name, ex.Message);
                var previous = await store.RecordCheckAsync(stack.Id, ex.Message, cancellationToken: cancellationToken);
                // Once per failure, not at every poll while it lasts.
                if (previous is null && alerts is not null)
                    await alerts.TaskFailedAsync(connection, $"Couldn't check the repository of the stack {stack.Name} on {connection.Name}", ex.Message,
                        StackLink(stack), cancellationToken);
                continue;
            }
            if (commit != repository.FetchedCommit)
            {
                LogNewCommit(logger, stack.Name, GitSource.Short(commit));
                Request(stack.Id, "poll");
            }
        }
    }

    private async Task TryStartAsync(PendingSync pending, CancellationToken cancellationToken)
    {
        var stack = await store.GetByIdAsync(pending.StackId, cancellationToken);
        if (stack is null || await store.GetRepositoryAsync(stack.Id, cancellationToken) is null || connections.Get(stack.EndpointId) is not { } connection)
        {
            _pending.TryRemove(pending.StackId, out _);
            return;
        }
        if (jobs.IsBusy(stack.EndpointId, stack.Name))
        {
            await GiveUpIfLateAsync(pending, stack, connection, $"Something else ran on {stack.Name} the whole time.", cancellationToken);
            return;
        }

        try
        {
            var job = await jobs.StartAsync(connection, stack.Name, StackOperation.Sync, new StackJobOptions(Automatic: true));
            _pending.TryRemove(pending.StackId, out _);
            LogStarted(logger, stack.Name, pending.By);
            _ = AuditWhenDoneAsync(job, connection, pending.By);
        }
        catch (StackJobRejectedException ex)
        {
            // Offline, busy, too many jobs: try again at the next tick, until it's too late.
            _pending[pending.StackId] = pending with { LastRejection = ex.Message };
            await GiveUpIfLateAsync(pending with { LastRejection = ex.Message }, stack, connection, ex.Message, cancellationToken);
        }
    }

    private async Task GiveUpIfLateAsync(PendingSync pending, StackDefinition stack, EngineConnection connection, string reason, CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - pending.RequestedAt < GiveUpAfter || !_pending.TryRemove(pending.StackId, out _))
            return;
        var message = $"A redeploy asked for by the {pending.By} didn't start within {GiveUpAfter.TotalMinutes:0} minutes: {reason}";
        LogGaveUp(logger, stack.Name, reason);
        await store.RecordCheckAsync(stack.Id, message, cancellationToken: cancellationToken);
        await audit.WriteAsync(new AuditRecord(null, pending.By, "Stacks: sync from git", stack.Name, connection.Id, connection.Name, AuditOutcome.Failed, message));
        if (alerts is not null)
            await alerts.TaskFailedAsync(connection, $"The stack {stack.Name} wasn't redeployed from git on {connection.Name}", message, StackLink(stack), cancellationToken);
    }

    /// <summary>The audit entry of an automatic sync, written when it ends so it names the commit and the outcome.</summary>
    private async Task AuditWhenDoneAsync(StackJob job, EngineConnection connection, string by)
    {
        try
        {
            StackJobView? last = null;
            await foreach (var view in job.WatchAsync(TimeSpan.FromSeconds(1)))
                last = view;
            var definition = await store.GetAsync(job.EndpointId, job.StackName);
            var repository = definition is null ? null : await store.GetRepositoryAsync(definition.Id);
            var commit = repository?.FetchedCommit is { } sha ? $"{GitSource.Short(sha)} ({repository.FetchedSubject}): " : "";
            var (outcome, detail) = last?.Status switch
            {
                StackJobSucceeded s => (AuditOutcome.Succeeded, s.Message),
                StackJobFailed f => (AuditOutcome.Failed, f.Error),
                _ => (AuditOutcome.Failed, "Cancelled."),
            };
            await audit.WriteAsync(new AuditRecord(null, by, "Stacks: sync from git", job.StackName, connection.Id, connection.Name, outcome, commit + detail));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Couldn't audit the sync of {Stack}", job.StackName);
        }
    }

    private static string StackLink(StackDefinition stack) => $"/endpoints/{stack.EndpointId}/stacks/{Uri.EscapeDataString(stack.Name)}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check the repository of the stack {Stack}: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string stack, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The branch of the stack {Stack} is at a new commit, {Commit}")]
    private static partial void LogNewCommit(ILogger logger, string stack, string commit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Syncing the stack {Stack} from git, as the {By} asked")]
    private static partial void LogStarted(ILogger logger, string stack, string by);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up redeploying the stack {Stack} from git: {Reason}")]
    private static partial void LogGaveUp(ILogger logger, string stack, string reason);
}
