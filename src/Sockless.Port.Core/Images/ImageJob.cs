using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;
using Sockless.Port.Work;

namespace Sockless.Port.Images;

public enum ImageJobKind
{
    Pull,
    Build,
}

/// <summary>Where a job is. Switch on it exhaustively.</summary>
public closed record ImageJobStatus;

public sealed record ImageJobRunning : ImageJobStatus;

/// <param name="Message">What happened, for people: "Downloaded newer image for nginx:1.27".</param>
/// <param name="ImageId">The built image, for builds.</param>
public sealed record ImageJobSucceeded(string Message, string? ImageId) : ImageJobStatus;

/// <param name="Error">The engine's message, as a sentence.</param>
public sealed record ImageJobFailed(string Error) : ImageJobStatus;

public sealed record ImageJobCanceled : ImageJobStatus;

/// <summary>What a watcher receives: the whole pull state, and the build log lines it hasn't seen yet.</summary>
/// <param name="LogStart">Absolute index of <paramref name="Log"/>'s first line (earlier lines may have been dropped).</param>
public sealed record ImageJobView(ImageJob Job, ImageJobStatus Status, PullSnapshot Pull, int LogStart, IReadOnlyList<string> Log);

/// <summary>
/// One pull or build running on an endpoint, independent of any browser: leaving the page doesn't
/// stop it. Thread-safe; the runner writes, any number of watchers read.
/// </summary>
public sealed partial class ImageJob
{
    /// <summary>Build output kept; older lines are dropped.</summary>
    public const int MaxLogLines = 5000;

    private readonly Lock _gate = new();
    private readonly PullTracker _tracker = new();
    private readonly List<string> _log = [];
    private readonly StringBuilder _partialLine = new();
    private readonly CancellationTokenSource _cancel;
    private int _logDropped;
    private long _reportedAt;
    private ImageJobStatus _status = new ImageJobRunning();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ImageJob(string id, int endpointId, ImageJobKind kind, string target, DateTimeOffset startedAt, CancellationToken stopping)
    {
        Id = id;
        EndpointId = endpointId;
        Kind = kind;
        Target = target;
        StartedAt = startedAt;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public string Id { get; }

    public int EndpointId { get; }

    public ImageJobKind Kind { get; }

    /// <summary>The reference being pulled, or the build's tags.</summary>
    public string Target { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public ImageJobStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsRunning => Status is ImageJobRunning;

    internal CancellationToken Token => _cancel.Token;

    /// <summary>The job in the list of background work, when there is one.</summary>
    internal WorkItem? Work { get; set; }

    /// <summary>Asks the job to stop. The engine aborts the pull or build when the request goes away.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The current state, with log lines from absolute index <paramref name="logFrom"/> on.</summary>
    public ImageJobView View(int logFrom = 0)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, logFrom - _logDropped);
            var start = _logDropped + Math.Min(skip, _log.Count);
            return new ImageJobView(this, _status, _tracker.Snapshot(), start, [.. _log.Skip(skip)]);
        }
    }

    /// <summary>
    /// The state now, then again whenever it changes (at most every <paramref name="interval"/>),
    /// until the job finishes. Log lines are sent once each.
    /// </summary>
    public async IAsyncEnumerable<ImageJobView> WatchAsync(TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var logFrom = 0;
        while (true)
        {
            Task changed;
            ImageJobView view;
            lock (_gate)
            {
                changed = _changed.Task;
                view = View(logFrom);
            }
            yield return view;
            logFrom = view.LogStart + view.Log.Count;
            if (view.Status is not ImageJobRunning)
                yield break;

            await changed.WaitAsync(cancellationToken);
            await Task.Delay(interval, cancellationToken);
        }
    }

    // ---------------------------------------------------------------- written by the runner

    internal void Apply(PullProgress progress)
    {
        PullSnapshot? snapshot = null;
        lock (_gate)
        {
            _tracker.Apply(progress);
            if (Work is not null && Due())
                snapshot = _tracker.Snapshot();
        }
        if (snapshot is not null)
            Work!.Report(snapshot.DownloadedBytes, snapshot.TotalBytes, WorkUnit.Bytes, PullDetail(snapshot));
        Changed();
    }

    private static string PullDetail(PullSnapshot snapshot) =>
        snapshot.Layers.Count == 0 ? "Starting" : $"{snapshot.LayersDone} of {snapshot.Layers.Count} layers";

    /// <summary>Progress goes to the work list at most four times a second.</summary>
    private bool Due()
    {
        var now = Environment.TickCount64;
        if (now - _reportedAt < 250)
            return false;
        _reportedAt = now;
        return true;
    }

    /// <summary>Build output arrives in fragments ("Step 1/6 : FROM x", then "\n"); lines are split on newlines.</summary>
    internal void AppendOutput(string text)
    {
        string? step = null;
        lock (_gate)
        {
            foreach (var ch in text)
            {
                if (ch == '\n')
                {
                    if (Work is not null && StepLine().IsMatch(_partialLine.ToString()))
                        step = _partialLine.ToString();
                    AddLineLocked();
                }
                else if (ch != '\r')
                {
                    _partialLine.Append(ch);
                }
            }
        }
        // The classic builder numbers its steps: "Step 3/6 : RUN make".
        if (step is not null && StepLine().Match(step) is { Success: true } match)
            Work?.Report(int.Parse(match.Groups[1].ValueSpan) - 1, int.Parse(match.Groups[2].ValueSpan), WorkUnit.Items, step.Trim());
        Changed();
    }

    [GeneratedRegex(@"^Step (\d{1,6})/(\d{1,6}) : ")]
    private static partial Regex StepLine();

    internal void Finish(ImageJobStatus status, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status is not ImageJobRunning)
                return;
            if (_partialLine.Length > 0)
                AddLineLocked();
            if (status is ImageJobSucceeded)
                _tracker.Complete();
            _status = status;
            FinishedAt = now;
        }
        switch (status)
        {
            case ImageJobSucceeded succeeded:
                Work?.Succeed(succeeded.Message);
                break;
            case ImageJobFailed failed:
                Work?.Fail(failed.Error);
                break;
            case ImageJobCanceled:
                Work?.Canceled();
                break;
            case ImageJobRunning:
                break;
        }
        Changed();
        _cancel.Dispose();
    }

    /// <summary>The engine's own summary of a pull ("Status: Image is up to date for …"), if it sent one.</summary>
    internal string? PullSummary()
    {
        lock (_gate)
        {
            return _tracker.Snapshot().Messages.LastOrDefault(m => m.StartsWith("Status: ", StringComparison.Ordinal)) is { } status
                ? status["Status: ".Length..]
                : null;
        }
    }

    private void AddLineLocked()
    {
        _log.Add(_partialLine.ToString());
        _partialLine.Clear();
        if (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
            _logDropped++;
        }
    }

    private void Changed()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }
}
