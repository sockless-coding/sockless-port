using Sockless.Port.Work;
using ShiftedTime = Sockless.Port.Core.Tests.ResourceMonitorTests.ShiftedTime;

namespace Sockless.Port.Core.Tests;

public sealed class WorkTrackerTests
{
    private readonly ShiftedTime _time = new();

    private WorkTracker Tracker() => new(_time);

    [Fact]
    public void Running_work_is_listed_before_queued_and_finished()
    {
        var tracker = Tracker();
        var done = tracker.Start(WorkKind.Pull, "Pulling nginx", 1, "local");
        done.Succeed("Pulled nginx.");
        var queued = tracker.Start(WorkKind.Scan, "Scanning redis", 1, "local", queued: true);
        var running = tracker.Start(WorkKind.UpdateCheck, "Checking", 1, "local");

        Assert.Equal([running.Id, queued.Id, done.Id], tracker.Snapshot().Select(v => v.Id));
        Assert.IsType<WorkQueued>(queued.View.State);
        queued.Running("Preparing");
        Assert.IsType<WorkRunning>(queued.View.State);
        Assert.Equal("Preparing", queued.View.Detail);
    }

    [Fact]
    public void Progress_is_kept_only_with_a_total_and_a_success_fills_the_bar()
    {
        var tracker = Tracker();
        var item = tracker.Start(WorkKind.Pull, "Pulling nginx");

        item.Report(10, 0);
        Assert.Null(item.View.Progress);

        item.Report(25, 100, WorkUnit.Bytes, "1 of 4 layers");
        Assert.Equal(new WorkProgress(25, 100, WorkUnit.Bytes), item.View.Progress);
        Assert.Equal(0.25, item.View.Progress!.Fraction);
        Assert.Equal("1 of 4 layers", item.View.Detail);

        item.Succeed("Pulled nginx.");
        Assert.Equal(100, item.View.Progress!.Done);
        Assert.NotNull(item.View.FinishedAt);
    }

    [Fact]
    public void Finished_work_ignores_later_reports_and_disposal()
    {
        var tracker = Tracker();
        var item = tracker.Start(WorkKind.Stack, "Deploying web");
        item.Fail("Compose failed.");

        item.Report(1, 2);
        item.Succeed("Deployed web.");
        item.Dispose();

        Assert.Equal(new WorkFailed("Compose failed."), item.View.State);
        Assert.Null(item.View.Progress);
    }

    [Fact]
    public void Disposing_active_work_marks_it_failed()
    {
        var tracker = Tracker();
        using (tracker.Start(WorkKind.RemoveImages, "Removing 3 images"))
        {
        }

        Assert.IsType<WorkFailed>(Assert.Single(tracker.Snapshot()).State);
    }

    [Fact]
    public void Finished_work_is_dropped_after_the_retention_and_forgotten_work_at_once()
    {
        var tracker = Tracker();
        tracker.Start(WorkKind.Backup, "Backing up").Succeed("Wrote it.");
        var active = tracker.Start(WorkKind.Scan, "Scanning", queued: true);
        var forgotten = tracker.Start(WorkKind.Stack, "Syncing web");
        forgotten.Forget();

        _time.Shift(WorkTracker.Retention + TimeSpan.FromMinutes(1));

        Assert.Equal([active.Id], tracker.Snapshot().Select(v => v.Id));
    }

    [Fact]
    public void Only_the_newest_finished_work_is_kept()
    {
        var tracker = Tracker();
        for (var i = 0; i < WorkTracker.MaxFinished + 5; i++)
            tracker.Start(WorkKind.Pull, $"Pulling {i}").Succeed("Pulled.");

        // Pruned on the next start or snapshot.
        Assert.Equal(WorkTracker.MaxFinished, tracker.Snapshot().Count);
    }

    [Fact]
    public void Every_change_is_announced_and_counted()
    {
        var tracker = Tracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        var item = tracker.Start(WorkKind.Pull, "Pulling nginx");
        item.Report(1, 2);
        item.Report(1, 2); // no change
        item.Succeed("Pulled.");

        Assert.Equal(3, changes);
        Assert.Equal(3, tracker.Version);
    }
}
