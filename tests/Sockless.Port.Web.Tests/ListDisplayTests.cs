using Sockless.Port.Docker.Models;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

/// <summary>How container and stack lists fit a row on one line.</summary>
public sealed class ListDisplayTests
{
    [Theory]
    [InlineData("Up 45 hours (healthy)", "Up 45 hours")]
    [InlineData("Up 3 seconds (health: starting)", "Up 3 seconds")]
    [InlineData("Up 2 hours (Paused)", "Up 2 hours")]
    [InlineData("Up About a minute", "Up About a minute")]
    [InlineData("Exited (1) 18 months ago", "18 months ago")]
    [InlineData("Exited (0) 2 hours ago", "2 hours ago")]
    [InlineData("Restarting (1) 5 seconds ago", "5 seconds ago")]
    [InlineData("Created", "")]
    [InlineData("Removal In Progress", "")]
    [InlineData(null, "")]
    public void Since_leaves_out_what_the_badge_says(string? status, string expected) =>
        Assert.Equal(expected, ContainerStatus.Since(status));

    [Fact]
    public void Health_takes_the_place_of_running()
    {
        var running = ContainerStatus.State("running", null);
        Assert.Equal(new StatusBadge("Healthy", "success"), ContainerStatus.Combined(running, ContainerStatus.Health("healthy")));
        Assert.Equal(new StatusBadge("Unhealthy", "danger"), ContainerStatus.Combined(running, ContainerStatus.Health("unhealthy")));
        Assert.Equal(new StatusBadge("Starting", "warning"), ContainerStatus.Combined(running, ContainerStatus.Health("starting")));
        Assert.Equal(running, ContainerStatus.Combined(running, null));

        var paused = ContainerStatus.State("paused", null);
        Assert.Equal(paused, ContainerStatus.Combined(paused, ContainerStatus.Health("healthy")));
    }

    [Fact]
    public void Compact_ports_say_only_what_differs()
    {
        IReadOnlyList<ContainerPort> ports =
        [
            new() { Ip = "0.0.0.0", PrivatePort = 80, PublicPort = 8080, Type = "tcp" },
            new() { Ip = "::", PrivatePort = 80, PublicPort = 8080, Type = "tcp" },
            new() { Ip = "0.0.0.0", PrivatePort = 443, PublicPort = 443, Type = "tcp" },
            new() { Ip = "127.0.0.1", PrivatePort = 53, PublicPort = 5353, Type = "udp" },
            new() { PrivatePort = 9000, Type = "tcp" },
        ];

        Assert.Equal(["443", "127.0.0.1:5353→53/udp", "8080→80"], ContainerStatus.CompactPorts(ports));
    }

    [Theory]
    [InlineData("ghcr.io/acme/web:1.2", "ghcr.io/acme/", "web:1.2")]
    [InlineData("192.168.11.237:32770/app:latest", "192.168.11.237:32770/", "app:latest")]
    [InlineData("postgres:17", "", "postgres:17")]
    [InlineData("acme/web@sha256:0123456789abcdef", "acme/", "web@sha256:0123456789abcdef")]
    [InlineData("sha256:0123456789abcdef0123", "", "0123456789ab")]
    [InlineData(null, "", "")]
    public void References_split_before_the_name(string? reference, string prefix, string name) =>
        Assert.Equal((prefix, name), ImageDisplay.SplitReference(reference));
}
