using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Streams;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Hubs;

/// <summary>
/// Pushes live endpoint state and engine events to browsers, and streams container and service logs, stats,
/// terminals, image jobs (pulls and builds) and stack jobs (Compose runs). Every client receives <c>endpointState</c>; <c>engineEvent</c> goes only to
/// clients watching that endpoint. Every call checks the user's role on its endpoint, read afresh
/// for each call, so a change applies to a connection that's already open.
/// </summary>
[Authorize]
public sealed class EngineHub(EngineConnections connections, TerminalSessions terminals, ImageJobs jobs, StackJobs stackJobs,
    AccessLoader accessLoader, AuditLog audit) : Hub
{
    public const string Path = "/hubs/engine";

    /// <summary>Most lines a log stream sends before following, so a huge log can't flood the browser.</summary>
    internal const int MaxTail = 5000;

    public static string Group(int endpointId) => $"endpoint-{endpointId}";

    public async Task Watch(int endpointId)
    {
        await RequireAsync(endpointId, EndpointPermission.View);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(endpointId));
    }

    public Task Unwatch(int endpointId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(endpointId));

    // ---------------------------------------------------------------- logs and stats

    /// <param name="Source">For service logs: the task the line came from, e.g. <c>web.2 on node-1</c>.</param>
    public sealed record LogMessage(string Stream, string Text, string? Time, string? Source = null);

    /// <summary>The last <paramref name="tail"/> lines, then new ones as they're written.</summary>
    public async IAsyncEnumerable<LogMessage> StreamLogs(int endpointId, string containerId, int tail, bool timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RequireAsync(endpointId, EndpointPermission.View);
        var zone = TimeZones.For(Context.GetHttpContext());
        var client = Client(endpointId);
        var tty = await IsTtyAsync(endpointId, client, containerId, cancellationToken);
        var options = new LogOptions { Follow = true, Tail = Math.Clamp(tail, 0, MaxTail), Timestamps = timestamps, Tty = tty };

        var lines = client.StreamLogsAsync(containerId, options, cancellationToken);
        await foreach (var line in Guard(lines, endpointId, cancellationToken))
        {
            yield return new LogMessage(
                line.Stream == StreamKind.Stderr ? "stderr" : "stdout",
                line.Text,
                line.Timestamp is { } time ? Format.Timestamp(time, zone) : null);
        }
    }

    /// <summary>
    /// The last <paramref name="tail"/> lines of every task of a swarm service, then new ones as
    /// they're written, each with the task it came from. Through a manager only.
    /// </summary>
    public async IAsyncEnumerable<LogMessage> StreamServiceLogs(int endpointId, string serviceId, int tail, bool timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RequireAsync(endpointId, EndpointPermission.View);
        var zone = TimeZones.For(Context.GetHttpContext());
        var client = Client(endpointId);
        Docker.Models.SwarmService service;
        Dictionary<string, string> sources;
        try
        {
            service = (await client.InspectServiceAsync(serviceId, cancellationToken)).Service;
            var nodes = (await client.ListNodesAsync(cancellationToken)).ToDictionary(n => n.Id, StringComparer.Ordinal);
            var tasks = await client.ListTasksAsync(new Dictionary<string, string[]> { ["service"] = [service.Id] }, cancellationToken);
            sources = tasks.ToDictionary(t => t.Id, t => TaskSource(t, service.Name, nodes), StringComparer.Ordinal);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            throw new HubException(EngineFailures.Describe(ex, Connection(endpointId)));
        }

        var options = new LogOptions
        {
            Follow = true,
            Tail = Math.Clamp(tail, 0, MaxTail),
            Timestamps = timestamps,
            Tty = service.Spec?.TaskTemplate?.ContainerSpec?.Tty ?? false,
        };
        await foreach (var line in Guard(client.StreamServiceLogsAsync(service.Id, options, cancellationToken), endpointId, cancellationToken))
        {
            yield return new LogMessage(
                line.Line.Stream == StreamKind.Stderr ? "stderr" : "stdout",
                line.Line.Text,
                line.Line.Timestamp is { } time ? Format.Timestamp(time, zone) : null,
                line.TaskId is { } task ? sources.GetValueOrDefault(task, SwarmDisplay.Short(task)) : null);
        }
    }

    private static string TaskSource(Docker.Models.SwarmTask task, string serviceName, IReadOnlyDictionary<string, Docker.Models.SwarmNode> nodes)
    {
        var name = SwarmDisplay.TaskName(task, serviceName, nodes);
        return task.Slot is not null && task.NodeId is { } node && nodes.TryGetValue(node, out var n) ? $"{name} on {n.DisplayName}" : name;
    }

    public sealed record StatsMessage(string Time, double Cpu, long Memory, long MemoryLimit, long NetworkRx, long NetworkTx, long BlockRead, long BlockWrite, long Pids);

    public async IAsyncEnumerable<StatsMessage> StreamStats(int endpointId, string containerId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RequireAsync(endpointId, EndpointPermission.View);
        var client = Client(endpointId);
        await foreach (var s in Guard(client.StreamStatsAsync(containerId, cancellationToken), endpointId, cancellationToken))
        {
            yield return new StatsMessage(s.Read.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Math.Round(s.CpuPercent, 2), s.MemoryUsage, s.MemoryLimit, s.NetworkRx, s.NetworkTx, s.BlockRead, s.BlockWrite, s.Pids);
        }
    }

    // ---------------------------------------------------------------- terminal

    /// <summary>Starts a shell in the container. Output arrives as <c>terminalOutput</c>, the end as <c>terminalExit</c>.</summary>
    /// <returns>The session id for input, resize and close.</returns>
    /// <remarks>A console is a shell in the container, so it needs the operator role, and every one opened is in the audit log.</remarks>
    public async Task<string> OpenTerminal(int endpointId, string containerId, string command, string? user, int columns, int rows)
    {
        var (connection, access) = await RequireAsync(endpointId, EndpointPermission.Operate, "Containers: open console", containerId);
        var client = Client(endpointId);
        var target = ShortId(containerId) + ": " + command + (string.IsNullOrEmpty(user) ? "" : " as " + user);
        try
        {
            var session = await terminals.OpenAsync(Context.ConnectionId, client, containerId, command, user, columns, rows, Context.ConnectionAborted);
            await WriteAuditAsync(access, connection, "Containers: open console", target);
            return session;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, Context.ConnectionAborted))
        {
            var message = EngineFailures.Describe(ex, connection);
            await WriteAuditAsync(access, connection, "Containers: open console", target, AuditOutcome.Failed, message);
            throw new HubException(message);
        }
    }

    public Task TerminalInput(string sessionId, string data) => terminals.InputAsync(Context.ConnectionId, sessionId, data);

    public Task TerminalResize(string sessionId, int columns, int rows) => terminals.ResizeAsync(Context.ConnectionId, sessionId, columns, rows);

    public Task CloseTerminal(string sessionId) => terminals.CloseAsync(Context.ConnectionId, sessionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await terminals.CloseAllAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ---------------------------------------------------------------- image jobs

    /// <summary>How often a job watcher gets an update while the job is busy.</summary>
    internal static readonly TimeSpan JobUpdateInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Starts pulling an image in the background (or joins the pull of it already running) and
    /// returns the job id. The pull goes on if the browser leaves; <see cref="CancelJob"/> stops it.
    /// </summary>
    /// <param name="onlyIfMissing">Finish at once when the image is already on the engine.</param>
    public async Task<string> StartPull(int endpointId, string reference, bool onlyIfMissing)
    {
        var (connection, access) = await RequireAsync(endpointId, EndpointPermission.Operate, "Images: pull", reference);
        if (ImageReference.Parse(reference) is null)
            throw new HubException("Enter an image such as nginx, nginx:1.27 or ghcr.io/org/app:tag.");
        try
        {
            var job = jobs.StartPull(connection, reference.Trim(), onlyIfMissing);
            await WriteAuditAsync(access, connection, "Images: pull", reference.Trim());
            return job.Id;
        }
        catch (ImageJobRejectedException ex)
        {
            await WriteAuditAsync(access, connection, "Images: pull", reference.Trim(), AuditOutcome.Failed, ex.Message);
            throw new HubException(ex.Message);
        }
    }

    /// <summary>The job's state now and on every change until it ends. Build output lines are sent once each.</summary>
    public async IAsyncEnumerable<JobMessage> WatchJob(string jobId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId) ?? throw new HubException("That pull or build is no longer known. It may have finished a while ago.");
        await RequireAsync(job.EndpointId, EndpointPermission.View);
        await foreach (var view in job.WatchAsync(JobUpdateInterval, cancellationToken))
            yield return JobMessage.From(view);
    }

    public async Task CancelJob(string jobId)
    {
        if (jobs.Get(jobId) is not { } job)
            return;
        var (connection, access) = await RequireAsync(job.EndpointId, EndpointPermission.Operate, "Images: cancel", job.Target);
        job.Cancel();
        await WriteAuditAsync(access, connection, "Images: cancel", job.Target);
    }

    public sealed record LayerMessage(string Id, string Phase, string Status, long Current, long Total);

    /// <param name="State"><c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>canceled</c>.</param>
    /// <param name="Message">The outcome, for people, once the job has ended.</param>
    public sealed record JobMessage(string Id, int EndpointId, string Kind, string Target, string State, string? Message, string? ImageId,
        IReadOnlyList<LayerMessage> Layers, IReadOnlyList<string> Messages, long Downloaded, long Total, int LayersDone, int LogStart, IReadOnlyList<string> Log)
    {
        public static JobMessage From(ImageJobView view)
        {
            var (state, message, imageId) = view.Status switch
            {
                ImageJobRunning => ("running", (string?)null, (string?)null),
                ImageJobSucceeded s => ("succeeded", s.Message, s.ImageId),
                ImageJobFailed f => ("failed", f.Error, null),
                ImageJobCanceled => ("canceled", "Cancelled.", null),
            };
            var pull = view.Pull;
            return new JobMessage(view.Job.Id, view.Job.EndpointId, view.Job.Kind == ImageJobKind.Pull ? "pull" : "build", view.Job.Target, state, message, imageId,
                [.. pull.Layers.Select(l => new LayerMessage(l.Id, l.Phase.ToString().ToLowerInvariant(), l.Status, l.Current, l.Total))],
                pull.Messages, pull.DownloadedBytes, pull.TotalBytes, pull.LayersDone, view.LogStart, view.Log);
        }
    }

    // ---------------------------------------------------------------- stack jobs

    /// <summary>A stack job's state now and on every change until it ends. Output lines are sent once each.</summary>
    public async IAsyncEnumerable<StackJobMessage> WatchStackJob(string jobId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var job = stackJobs.Get(jobId) ?? throw new HubException("That stack job is no longer known. It may have finished a while ago.");
        await RequireAsync(job.EndpointId, EndpointPermission.View);
        await foreach (var view in job.WatchAsync(JobUpdateInterval, cancellationToken))
            yield return StackJobMessage.From(view);
    }

    public async Task CancelStackJob(string jobId)
    {
        if (stackJobs.Get(jobId) is not { } job)
            return;
        var (connection, access) = await RequireAsync(job.EndpointId, EndpointPermission.Operate, "Stacks: cancel", job.StackName);
        job.Cancel();
        await WriteAuditAsync(access, connection, "Stacks: cancel", job.StackName);
    }

    /// <param name="State"><c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>canceled</c>.</param>
    /// <param name="Badge">The badge's word, as the page renders it.</param>
    /// <param name="Summary">What's happening or how it ended, for people.</param>
    public sealed record StackJobMessage(string Id, string Stack, string Operation, string State, string Badge, string Summary,
        IReadOnlyList<string> Hints, int LogStart, IReadOnlyList<string> Log)
    {
        public static StackJobMessage From(StackJobView view)
        {
            var state = view.Status switch
            {
                StackJobRunning => "running",
                StackJobSucceeded => "succeeded",
                StackJobFailed => "failed",
                StackJobCanceled => "canceled",
            };
            var operation = view.Job.Operation;
            return new StackJobMessage(view.Job.Id, view.Job.StackName, StackDisplay.Key(operation), state,
                StackDisplay.JobBadge(view.Status, operation).Label, StackDisplay.JobSummary(view.Status, operation),
                view.Status is StackJobFailed failed ? failed.Hints : [], view.LogStart, view.Log);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The endpoint, if the user may do <paramref name="permission"/> there. An endpoint they may
    /// not see is unknown to them. Refused changes (those with an <paramref name="action"/>) are audited.
    /// </summary>
    private async Task<(EngineConnection Connection, UserAccess Access)> RequireAsync(int endpointId, EndpointPermission permission,
        string? action = null, string? target = null)
    {
        var access = await accessLoader.LoadAsync(Context.User!, Context.ConnectionAborted)
                     ?? throw new HubException("Your session has ended. Reload the page and sign in again.");
        if (connections.Get(endpointId) is not { } connection || !access.CanView(endpointId))
            throw new HubException("Unknown endpoint.");
        if (!access.Can(endpointId, permission))
        {
            var role = permission == EndpointPermission.ManageSwarm ? "swarm operator" : "operator";
            var reason = "Needs the " + role + " role on " + connection.Name + ".";
            if (action is not null)
                await WriteAuditAsync(access, connection, action, target is null ? null : ShortId(target), AuditOutcome.Denied, reason);
            throw new HubException("You can't do that here: it needs the " + role + " role on " + connection.Name + ".");
        }
        return (connection, access);
    }

    private Task WriteAuditAsync(UserAccess access, EngineConnection connection, string action, string? target,
        AuditOutcome outcome = AuditOutcome.Succeeded, string? detail = null) =>
        audit.WriteAsync(new AuditRecord(access.UserId, access.UserName, action, target, connection.Id, connection.Name, outcome, detail,
            Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString()));

    private static string ShortId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigitLower) ? id[..12] : id;

    private EngineConnection Connection(int endpointId) =>
        connections.Get(endpointId) ?? throw new HubException("Unknown endpoint.");

    private DockerClient Client(int endpointId) =>
        Connection(endpointId).Client ?? throw new HubException("The engine is offline.");

    private async Task<bool> IsTtyAsync(int endpointId, DockerClient client, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.InspectContainerAsync(containerId, cancellationToken)).Container.Config?.Tty ?? false;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            throw new HubException(EngineFailures.Describe(ex, Connection(endpointId)));
        }
    }

    /// <summary>Turns engine failures mid-stream into a <see cref="HubException"/> the browser can show.</summary>
    private async IAsyncEnumerable<T> Guard<T>(IAsyncEnumerable<T> source, int endpointId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                throw new HubException(EngineFailures.Describe(ex, Connection(endpointId)));
            }
            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
    }
}
