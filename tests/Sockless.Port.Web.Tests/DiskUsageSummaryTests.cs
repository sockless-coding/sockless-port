using Sockless.Port.Docker.Models;
using Sockless.Port.Web.Infrastructure;
using Sockless.Port.Web.Pages.Engine;

namespace Sockless.Port.Web.Tests;

public sealed class DiskUsageSummaryTests
{
    [Fact]
    public void Counts_in_use_and_reclaimable_space_per_type()
    {
        var df = new DiskUsage
        {
            LayersSize = 300,
            Images = [new() { Size = 200, SharedSize = 50, Containers = 1 }, new() { Size = 100, SharedSize = 20, Containers = 0 }],
            Containers = [new() { State = "running", SizeRw = 10 }, new() { State = "exited", SizeRw = 5 }],
            Volumes = [new() { UsageData = new() { Size = 40, RefCount = 1 } }, new() { UsageData = new() { Size = 60, RefCount = 0 } }, new() { UsageData = new() { Size = -1, RefCount = -1 } }],
            BuildCache = [new() { Size = 7, InUse = true }, new() { Size = 3 }, new() { Size = 100, Shared = true }],
        };

        var summary = DiskUsageSummary.From(df);

        Assert.Equal(
            [
                new DiskUsageRow("Images", 2, 1, 300, 80),
                new DiskUsageRow("Containers", 2, 1, 15, 5),
                new DiskUsageRow("Volumes", 3, 1, 100, 60),
                new DiskUsageRow("Build cache", 3, 1, 10, 3),
            ],
            summary.Rows);
    }

    [Fact]
    public void Podman_without_build_cache_has_no_build_cache_row()
    {
        var summary = DiskUsageSummary.From(new DiskUsage { Images = [], Containers = [], Volumes = [], BuildCache = null });

        Assert.DoesNotContain(summary.Rows, r => r.Kind == "Build cache");
        Assert.Equal(0, summary.TotalSize);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1 kB")]
    [InlineData(1_234_567, "1.23 MB")]
    [InlineData(45_600_000_000, "45.6 GB")]
    [InlineData(-5, "0 B")]
    public void Formats_sizes_like_docker(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Formats_memory_in_binary_units() => Assert.Equal("15.6 GiB", Format.BinaryBytes(16_750_000_000));
}
