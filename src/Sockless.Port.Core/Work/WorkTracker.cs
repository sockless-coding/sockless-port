using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Sockless.Port.Work;

/// <summary>What a piece of background work is, for its icon, wording and link.</summary>
public enum WorkKind
{
    Pull,
    Build,
    Stack,
    Scan,
    UpdateCheck,
    RemoveImages,
    PruneImages,
    Prune,
    Backup,
}

/// <summary>Where a piece of work is. Switch on it exhaustively.</summary>
public closed record WorkState;

/// <summary>Waiting its turn (a scan behind others).</summary>
public sealed record WorkQueued : WorkState;

public sealed record WorkRunning : WorkState;

/// <param name="Message">How it ended, for people: "Pulled nginx:1.27."</param>
public sealed record WorkSucceeded(string Message) : WorkState;

/// <param name="Error">What went wrong, as a sentence.</param>
public sealed record WorkFailed(string Error) : WorkState;

public sealed record WorkCanceled : WorkState;

public enum WorkUnit
{
    Items,
    Bytes,
}

/// <summary>How far along a piece of work is, when it can tell. <paramref name="Total"/> is above zero.</summary>
public sealed record WorkProgress(long Done, long Total, WorkUnit Unit)
{
    public double Fraction => Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>A piece of work as it is now. Immutable: a copy taken under the item's lock.</summary>
/// <param name="EndpointId">The endpoint it runs on; null for work that isn't about one (backups), which only administrators see.</param>
/// <param name="Subject">What it's about, for the link: an image ID, a stack name, a build job's ID.</param>
/// <param name="Detail">What it's doing now: a phase, the last output line.</param>
/// <param name="By">Who started it: a user name, or <c>schedule</c>; null when not known.</param>
public sealed record WorkView(string Id, int? EndpointId, string? EndpointName, WorkKind Kind, string Title, string? Subject, string? Detail,
    WorkProgress? Progress, WorkState State, string? By, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public bool IsActive => State is WorkQueued or WorkRunning;
}

/// <summary>
/// One piece of background work, reported by whatever runs it. Thread-safe. Finish it with
/// <see cref="Succeed"/>, <see cref="Fail"/> or <see cref="Canceled"/>; disposing one that's still
/// active marks it failed, so an exception never leaves it running for ever.
/// </summary>
public sealed class WorkItem : IDisposable
{
    private readonly Lock _gate = new();
    private readonly WorkTracker _tracker;
    private WorkView _view;

    internal WorkItem(WorkTracker tracker, WorkView view)
    {
        _tracker = tracker;
        _view = view;
    }

    public string Id => _view.Id;

    public WorkView View
    {
        get { lock (_gate) return _view; }
    }

    /// <summary>Out of the queue and under way.</summary>
    public void Running(string? detail = null) => Update(v => v with { State = new WorkRunning(), Detail = detail ?? v.Detail });

    public void Retitle(string title) => Update(v => v.Title == title ? v : v with { Title = title });

    public void Describe(string? detail) => Update(v => v.Detail == detail ? v : v with { Detail = detail });

    /// <summary>How far along it is; a total of zero or less means it can't tell.</summary>
    public void Report(long done, long total, WorkUnit unit = WorkUnit.Items, string? detail = null) =>
        Update(v => v with { Progress = total > 0 ? new WorkProgress(done, total, unit) : null, Detail = detail ?? v.Detail });

    public void Succeed(string message) => End(new WorkSucceeded(message));

    public void Fail(string error) => End(new WorkFailed(error));

    public void Canceled() => End(new WorkCanceled());

    public void Dispose() => End(new WorkFailed("It stopped before it finished."));

    /// <summary>Takes it off the list at once, for work that turned out to be nothing worth showing.</summary>
    public void Forget() => _tracker.Remove(Id);

    private void End(WorkState state) => Update(v => v with
    {
        State = state,
        FinishedAt = _tracker.Now,
        // A finished bar is full, or gone.
        Progress = state is WorkSucceeded && v.Progress is { } p ? p with { Done = p.Total } : state is WorkSucceeded ? null : v.Progress,
    });

    private void Update(Func<WorkView, WorkView> change)
    {
        lock (_gate)
        {
            if (!_view.IsActive)
                return;
            var next = change(_view);
            if (next == _view)
                return;
            _view = next;
        }
        _tracker.OnChanged();
    }
}

/// <summary>
/// What's going on in the background, across endpoints and pages: pulls and builds, stack runs,
/// scans, update checks, removals, prunes and backups. Whatever runs the work reports it here;
/// the UI lists it and is told when it changes. Kept in memory: finished work stays visible for
/// <see cref="Retention"/>, and at most <see cref="MaxFinished"/> of it.
/// </summary>
public sealed class WorkTracker(TimeProvider time)
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    public const int MaxFinished = 100;

    private readonly ConcurrentDictionary<string, WorkItem> _items = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>
    /// Raised after any change, on the thread that made it, possibly many times a second (pull
    /// progress). Handlers must be quick and must not throw.
    /// </summary>
    public event Action? Changed;

    /// <summary>Goes up with every change, so a reader can tell whether it has seen the latest.</summary>
    public long Version => Interlocked.Read(ref _version);

    internal DateTimeOffset Now => time.GetUtcNow();

    /// <param name="queued">It waits for its turn; call <see cref="WorkItem.Running"/> when it starts.</param>
    public WorkItem Start(WorkKind kind, string title, int? endpointId = null, string? endpointName = null, string? subject = null, string? by = null,
        bool queued = false)
    {
        Prune();
        var view = new WorkView(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), endpointId, endpointName, kind, title, subject, null, null,
            queued ? new WorkQueued() : new WorkRunning(), by, time.GetUtcNow(), null);
        var item = new WorkItem(this, view);
        _items[item.Id] = item;
        OnChanged();
        return item;
    }

    /// <summary>Everything still listed: active work first (running before queued), then the newest finished.</summary>
    public IReadOnlyList<WorkView> Snapshot()
    {
        Prune();
        return [.. _items.Values.Select(i => i.View)
            .OrderBy(v => v.State switch { WorkRunning => 0, WorkQueued => 1, _ => 2 })
            .ThenByDescending(v => v.FinishedAt ?? v.StartedAt)];
    }

    internal void Remove(string id)
    {
        if (_items.TryRemove(id, out _))
            OnChanged();
    }

    internal void OnChanged()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        var finished = _items.Values.Select(i => i.View).Where(v => !v.IsActive).OrderByDescending(v => v.FinishedAt).ToList();
        var removed = false;
        for (var i = 0; i < finished.Count; i++)
        {
            if ((i >= MaxFinished || finished[i].FinishedAt < cutoff) && _items.TryRemove(finished[i].Id, out _))
                removed = true;
        }
        if (removed)
            Interlocked.Increment(ref _version);
    }
}
