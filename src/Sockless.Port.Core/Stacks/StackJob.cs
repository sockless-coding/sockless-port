using System.Runtime.CompilerServices;

namespace Sockless.Port.Stacks;

public enum StackOperation
{
    /// <summary><c>up --detach --remove-orphans</c>: create or update what changed.</summary>
    Deploy,

    /// <summary>Deploy after pulling every image again (<c>--pull always</c>).</summary>
    Update,

    Start,
    Stop,
    Restart,

    /// <summary><c>down --remove-orphans</c>, optionally with <c>--volumes</c>.</summary>
    Down,

    /// <summary>Optionally down, then forget the stack and delete its files.</summary>
    Remove,

    /// <summary>A git-backed stack: fetch its branch and take the files from the newest commit. Nothing is deployed.</summary>
    Fetch,

    /// <summary>A git-backed stack: fetch, then deploy (automatic syncs only deploy a deployed stack whose files changed).</summary>
    Sync,
}

/// <summary>Where a stack job is. Switch on it exhaustively.</summary>
public closed record StackJobStatus;

public sealed record StackJobRunning : StackJobStatus;

/// <param name="Message">What happened, for people: "Deployed web."</param>
public sealed record StackJobSucceeded(string Message) : StackJobStatus;

/// <param name="Error">What failed, as a sentence.</param>
/// <param name="Hints">What to do about problems recognised in the output (<see cref="ComposeHints"/>).</param>
public sealed record StackJobFailed(string Error, IReadOnlyList<string> Hints) : StackJobStatus;

public sealed record StackJobCanceled : StackJobStatus;

/// <param name="LogStart">Absolute index of <paramref name="Log"/>'s first line.</param>
public sealed record StackJobView(StackJob Job, StackJobStatus Status, int LogStart, IReadOnlyList<string> Log);

/// <summary>
/// One Compose run on a stack, independent of any browser: leaving the page doesn't stop it.
/// Thread-safe; the runner writes, any number of watchers read.
/// </summary>
public sealed class StackJob
{
    public const int MaxLogLines = 5000;

    private readonly Lock _gate = new();
    private readonly List<string> _log = [];
    private readonly CancellationTokenSource _cancel;
    private int _logDropped;
    private StackJobStatus _status = new StackJobRunning();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StackJob(string id, int endpointId, string stackName, StackOperation operation, DateTimeOffset startedAt, CancellationToken stopping)
    {
        Id = id;
        EndpointId = endpointId;
        StackName = stackName;
        Operation = operation;
        StartedAt = startedAt;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public string Id { get; }

    public int EndpointId { get; }

    public string StackName { get; }

    public StackOperation Operation { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public StackJobStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsRunning => Status is StackJobRunning;

    internal CancellationToken Token => _cancel.Token;

    /// <summary>Asks the job to stop: Compose is killed, and whatever it already did stays done.</summary>
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

    /// <summary>The state now, with log lines from absolute index <paramref name="logFrom"/> on.</summary>
    public StackJobView View(int logFrom = 0)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, logFrom - _logDropped);
            var start = _logDropped + Math.Min(skip, _log.Count);
            return new StackJobView(this, _status, start, [.. _log.Skip(skip)]);
        }
    }

    /// <summary>The state now, then on each change (at most every <paramref name="interval"/>) until the job ends. Lines are sent once each.</summary>
    public async IAsyncEnumerable<StackJobView> WatchAsync(TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var logFrom = 0;
        while (true)
        {
            Task changed;
            StackJobView view;
            lock (_gate)
            {
                changed = _changed.Task;
                view = View(logFrom);
            }
            yield return view;
            logFrom = view.LogStart + view.Log.Count;
            if (view.Status is not StackJobRunning)
                yield break;

            await changed.WaitAsync(cancellationToken);
            await Task.Delay(interval, cancellationToken);
        }
    }

    internal void AppendLine(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
                _logDropped++;
            }
        }
        Changed();
    }

    internal void Finish(StackJobStatus status, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status is not StackJobRunning)
                return;
            _status = status;
            FinishedAt = now;
        }
        Changed();
        _cancel.Dispose();
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
