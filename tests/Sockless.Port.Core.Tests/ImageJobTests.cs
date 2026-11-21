using Sockless.Port.Docker.Models;
using Sockless.Port.Images;

namespace Sockless.Port.Core.Tests;

public sealed class ImageJobTests
{
    private static ImageJob NewJob(ImageJobKind kind = ImageJobKind.Build) =>
        new("job1", 1, kind, "app:1", DateTimeOffset.UtcNow, CancellationToken.None);

    [Fact]
    public void Build_output_fragments_are_joined_into_lines()
    {
        var job = NewJob();

        // As Docker sends it: the step, then its newline in a separate message.
        job.AppendOutput("Step 1/2 : FROM busybox:1.37");
        job.AppendOutput("\n");
        job.AppendOutput(" ---> a7dbc319b143\r\nStep 2/2");
        job.Finish(new ImageJobSucceeded("Built app:1.", "sha256:x"), DateTimeOffset.UtcNow);

        Assert.Equal(["Step 1/2 : FROM busybox:1.37", " ---> a7dbc319b143", "Step 2/2"], job.View().Log);
    }

    [Fact]
    public void Long_logs_keep_the_newest_lines_and_count_the_dropped_ones()
    {
        var job = NewJob();
        for (var i = 0; i < ImageJob.MaxLogLines + 10; i++)
            job.AppendOutput($"line {i}\n");

        var view = job.View();
        Assert.Equal(ImageJob.MaxLogLines, view.Log.Count);
        Assert.Equal(10, view.LogStart);
        Assert.Equal("line 10", view.Log[0]);
        // Asking from an absolute index returns only what follows.
        Assert.Equal(["line 5009"], job.View(ImageJob.MaxLogLines + 9).Log);
    }

    [Fact]
    public async Task Watchers_get_each_line_once_and_the_end()
    {
        var job = NewJob();
        job.AppendOutput("first\n");
        var views = new List<ImageJobView>();
        var watching = Task.Run(async () =>
        {
            await foreach (var view in job.WatchAsync(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken))
                views.Add(view);
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        job.AppendOutput("second\n");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        job.Finish(new ImageJobFailed("Step 2 failed."), DateTimeOffset.UtcNow);
        await watching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], views.SelectMany(v => v.Log));
        Assert.Equal(new ImageJobFailed("Step 2 failed."), views[^1].Status);
    }

    [Fact]
    public void A_finished_job_keeps_its_first_outcome()
    {
        var job = NewJob(ImageJobKind.Pull);
        job.Apply(new PullProgress { Id = "abc", Status = "Download complete" });

        job.Finish(new ImageJobSucceeded("Pulled app:1.", null), DateTimeOffset.UtcNow);
        job.Finish(new ImageJobCanceled(), DateTimeOffset.UtcNow);
        job.Cancel();

        Assert.IsType<ImageJobSucceeded>(job.Status);
        Assert.False(job.IsRunning);
        Assert.Equal(Docker.Streams.LayerPhase.Done, Assert.Single(job.View().Pull.Layers).Phase);
    }
}
