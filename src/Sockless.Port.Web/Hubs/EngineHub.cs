using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Streams;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Hubs;

/// <summary>
/// Pushes live endpoint state and engine events to browsers, and streams container logs, stats,
/// terminals and image jobs (pulls and builds). Every client receives <c>endpointState</c>; <c>engineEvent</c> goes only to
/// clients watching that endpoint.
/// </summary>
[Authorize]
public sealed class EngineHub(EngineConnections connections, TerminalSessions terminals, ImageJobs jobs) : Hub
{
    public const string Path = "/hubs/engine";

    /// <summary>Most lines a log stream sends before following, so a huge log can't flood the browser.</summary>
    internal const int MaxTail = 5000;

    public static string Group(int endpointId) => $"endpoint-{endpointId}";

    public async Task Watch(int endpointId)
    {
        Connection(endpointId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(endpointId));
    }

    public Task Unwatch(int endpointId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(endpointId));

    // ---------------------------------------------------------------- logs and stats

    public sealed record LogMessage(string Stream, string Text, string? Time);

    /// <summary>The last <paramref name="tail"/> lines, then new ones as they're written.</summary>
    public async IAsyncEnumerable<LogMessage> StreamLogs(int endpointId, string containerId, int tail, bool timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = Client(endpointId);
        var tty = await IsTtyAsync(endpointId, client, containerId, cancellationToken);
        var options = new LogOptions { Follow = true, Tail = Math.Clamp(tail, 0, MaxTail), Timestamps = timestamps, Tty = tty };

        var lines = client.StreamLogsAsync(containerId, options, cancellationToken);
        await foreach (var line in Guard(lines, endpointId, cancellationToken))
        {
            yield return new LogMessage(
                line.Stream == StreamKind.Stderr ? "stderr" : "stdout",
                line.Text,
                line.Timestamp is { } time ? Format.Timestamp(time) : null);
        }
    }

    public sealed record StatsMessage(string Time, double Cpu, long Memory, long MemoryLimit, long NetworkRx, long NetworkTx, long BlockRead, long BlockWrite, long Pids);

    public async IAsyncEnumerable<StatsMessage> StreamStats(int endpointId, string containerId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
    public async Task<string> OpenTerminal(int endpointId, string containerId, string command, string? user, int columns, int rows)
    {
        var client = Client(endpointId);
        try
        {
            return await terminals.OpenAsync(Context.ConnectionId, client, containerId, command, user, columns, rows, Context.ConnectionAborted);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, Context.ConnectionAborted))
        {
            throw new HubException(EngineFailures.Describe(ex, Connection(endpointId)));
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
    public string StartPull(int endpointId, string reference, bool onlyIfMissing)
    {
        if (ImageReference.Parse(reference) is null)
            throw new HubException("Enter an image such as nginx, nginx:1.27 or ghcr.io/org/app:tag.");
        try
        {
            return jobs.StartPull(Connection(endpointId), reference.Trim(), onlyIfMissing).Id;
        }
        catch (ImageJobRejectedException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <summary>The job's state now and on every change until it ends. Build output lines are sent once each.</summary>
    public async IAsyncEnumerable<JobMessage> WatchJob(string jobId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId) ?? throw new HubException("That pull or build is no longer known. It may have finished a while ago.");
        await foreach (var view in job.WatchAsync(JobUpdateInterval, cancellationToken))
            yield return JobMessage.From(view);
    }

    public void CancelJob(string jobId) => jobs.Get(jobId)?.Cancel();

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

    // ---------------------------------------------------------------- helpers

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
