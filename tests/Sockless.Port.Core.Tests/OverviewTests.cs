using Sockless.Port.Docker.Models;
using Sockless.Port.Monitoring;
using Sockless.Port.Overview;
using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Core.Tests;

public sealed class OverviewTests
{
    private static ContainerSummary Container(string name, string state, string status, bool helper = false) => new()
    {
        Id = name + "-id",
        Names = ["/" + name],
        State = state,
        Status = status,
        Labels = helper ? new Dictionary<string, string> { ["sockless-port.helper"] = "volume-browser" } : null,
    };

    [Fact]
    public void Containers_are_counted_by_state_with_unhealthy_ones_named()
    {
        var counts = EndpointSummaries.Count(
        [
            Container("web", "running", "Up 2 hours (healthy)"),
            Container("worker", "running", "Up 5 minutes (unhealthy)"),
            Container("api", "running", "Up 1 minute (unhealthy)"),
            Container("cron", "restarting", "Restarting (1) 3 seconds ago"),
            Container("db", "paused", "Up 1 hour (Paused)"),
            Container("job", "exited", "Exited (0) 1 hour ago"),
            Container("broken", "exited", "Exited (1) 2 minutes ago"),
            Container("new", "created", "Created"),
        ]);

        Assert.Equal(3 + 1, counts.Running);
        Assert.Equal(1, counts.Paused);
        Assert.Equal(3, counts.Stopped);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(8, counts.Total);
        Assert.Equal(["api", "worker"], counts.Unhealthy);
    }

    [Theory]
    [InlineData("Exited (1) 2 minutes ago", true)]
    [InlineData("Exited (137) 2 minutes ago", true)]
    [InlineData("Exited (0) 2 minutes ago", false)]
    [InlineData("Created", false)]
    [InlineData(null, false)]
    public void An_exit_code_other_than_zero_is_an_error(string? status, bool expected) =>
        Assert.Equal(expected, EndpointSummaries.ExitedWithError(status));

    [Fact]
    public void A_stopped_container_with_an_unhealthy_status_is_not_unhealthy() =>
        Assert.False(Container("old", "exited", "Exited (1) (unhealthy)").IsUnhealthy);

    [Fact]
    public void Findings_add_up_the_open_counts_of_scanned_images()
    {
        ScanSummary Scan(string id, int critical, int high) =>
            new(id, DateTimeOffset.UnixEpoch, new ScanCounts(critical, high, 4, 0, 0, 1), "debian 12", false, "0.69.3", null);

        var findings = EndpointSummaries.Findings([Scan("sha256:a", 2, 5), Scan("sha256:b", 0, 1), Scan("sha256:c", 1, 0)], images: 7);

        Assert.Equal(new FindingCounts(Critical: 3, High: 6, ImagesWithCritical: 2, Scanned: 3, Images: 7), findings);
    }

    [Fact]
    public void Usage_is_a_share_of_the_host()
    {
        var snapshot = new UsageSnapshot(DateTimeOffset.UnixEpoch, Cpus: 4, HostMemory: 8_000, Running: 2,
        [
            new ContainerUsage("a", "web", null, true, 150, 1_000, 0, 0, 0, 0, 0, 1),
            new ContainerUsage("b", "db", null, true, 50, 3_000, 0, 0, 0, 0, 0, 1),
        ]);

        var usage = EndpointSummaries.Usage(snapshot);

        Assert.NotNull(usage);
        Assert.Equal(200, usage.Cpu);
        Assert.Equal(50, usage.CpuShare);     // two of four CPUs
        Assert.Equal(4_000, usage.Memory);
        Assert.Equal(50, usage.MemoryShare);
    }

    [Fact]
    public void A_failed_or_missing_snapshot_shows_no_usage()
    {
        Assert.Null(EndpointSummaries.Usage(null));
        Assert.Null(EndpointSummaries.Usage(new UsageSnapshot(DateTimeOffset.UnixEpoch, 4, 8_000, 0, [], "The engine is offline.")));
        Assert.Null(new UsageTotals(DateTimeOffset.UnixEpoch, 30, 0, 10, 0).CpuShare);
    }
}
