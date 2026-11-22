using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Networks;
using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

/// <summary>The create forms for volumes and networks, the file browser's parsing, and usage lookups.</summary>
public sealed class VolumeNetworkSpecTests
{
    private static readonly string[] AllDrivers = ["bridge", "macvlan", "ipvlan", "overlay"];

    // ---------------------------------------------------------------- volumes

    [Fact]
    public void Builds_a_volume_with_driver_options_and_labels()
    {
        var (request, errors) = VolumeCreateSpecBuilder.Build(new VolumeCreateSpec
        {
            Name = " media ",
            Driver = "local",
            Options = "type=nfs\no=addr=10.0.0.5,rw\n# comment\ndevice=:/export",
            Labels = "team=infra",
        });

        Assert.Empty(errors);
        Assert.Equal("media", request!.Name);
        Assert.Equal(new Dictionary<string, string> { ["type"] = "nfs", ["o"] = "addr=10.0.0.5,rw", ["device"] = ":/export" }, request.DriverOpts);
        Assert.Equal("infra", request.Labels!["team"]);
    }

    [Fact]
    public void An_empty_volume_form_gets_a_generated_name_and_the_local_driver()
    {
        var (request, errors) = VolumeCreateSpecBuilder.Build(new VolumeCreateSpec { Driver = "" });

        Assert.Empty(errors);
        Assert.Null(request!.Name);
        Assert.Equal("local", request.Driver);
        Assert.Null(request.DriverOpts);
    }

    [Theory]
    [InlineData("a", "Name")]
    [InlineData("-data", "Name")]
    [InlineData("my data", "Name")]
    public void Volume_names_follow_the_engine_rule(string name, string field) =>
        Assert.Equal(field, Assert.Single(VolumeCreateSpecBuilder.Build(new VolumeCreateSpec { Name = name }).Errors).Field);

    [Fact]
    public void Bad_option_lines_name_the_line() =>
        Assert.Equal(new SpecErrorView("Options", "Line 2: use key=value; a key has no spaces."),
            SpecErrorView.Of(Assert.Single(VolumeCreateSpecBuilder.Build(new VolumeCreateSpec { Options = "type=nfs\nbad key=1" }).Errors)));

    // ---------------------------------------------------------------- networks

    [Fact]
    public void Builds_a_bridge_network_with_addresses()
    {
        var (request, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec
        {
            Name = "backend",
            Driver = "bridge",
            Subnet = "172.30.0.0/24",
            Gateway = "172.30.0.1",
            IpRange = "172.30.0.128/25",
            EnableIpv6 = true,
            Ipv6Subnet = "fd00:30::/64",
            Internal = true,
            Options = "com.docker.network.driver.mtu=1400",
        }, AllDrivers);

        Assert.Empty(errors);
        Assert.Equal(("backend", "bridge", true, true), (request!.Name, request.Driver, request.Internal, request.EnableIpv6));
        Assert.Collection(request.Ipam!.Config!,
            v4 => Assert.Equal(("172.30.0.0/24", "172.30.0.1", "172.30.0.128/25"), (v4.Subnet, v4.Gateway, v4.IpRange)),
            v6 => Assert.Equal("fd00:30::/64", v6.Subnet));
        Assert.Equal("1400", request.Options!["com.docker.network.driver.mtu"]);
    }

    [Fact]
    public void Without_addresses_the_engine_picks_them()
    {
        var (request, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "web" }, AllDrivers);

        Assert.Empty(errors);
        Assert.Null(request!.Ipam);
        Assert.Null(request.Options);
    }

    [Fact]
    public void A_macvlan_parent_becomes_the_parent_option()
    {
        var (request, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "lan", Driver = "macvlan", Parent = "eth0.10", Subnet = "192.168.10.0/24" }, AllDrivers);

        Assert.Empty(errors);
        Assert.Equal("eth0.10", request!.Options!["parent"]);
    }

    [Theory]
    [InlineData("", "bridge", null, null, null, "Name", "Enter a name, e.g. backend.")]
    [InlineData("web", "overlay", null, null, null, "Driver", "Choose bridge, macvlan.")]
    [InlineData("web", "bridge", "172.30.0.0", null, null, "Subnet", "Add the prefix length, e.g. 172.30.0.0/24.")]
    [InlineData("web", "bridge", "172.30.0.5/24", null, null, "Subnet", "Use the network's first address: 172.30.0.0/24.")]
    [InlineData("web", "bridge", "nonsense/8", null, null, "Subnet", "Enter a subnet in CIDR form, e.g. 172.20.0.0/16.")]
    [InlineData("web", "bridge", "172.30.0.0/24", "172.31.0.1", null, "Gateway", "172.31.0.1 isn't in 172.30.0.0/24.")]
    [InlineData("web", "bridge", "172.30.0.0/24", null, "172.30.0.0/16", "IpRange", "The range must be inside 172.30.0.0/24, e.g. a smaller block of it.")]
    [InlineData("web", "bridge", null, "172.30.0.1", null, "Subnet", "Set the subnet too: a gateway or range needs one.")]
    public void Network_errors_point_at_the_field(string name, string driver, string? subnet, string? gateway, string? range, string field, string message)
    {
        var (request, errors) = NetworkCreateSpecBuilder.Build(
            new NetworkCreateSpec { Name = name, Driver = driver, Subnet = subnet, Gateway = gateway, IpRange = range }, ["bridge", "macvlan"]);

        Assert.Null(request);
        Assert.Equal(new SpecErrorView(field, message), SpecErrorView.Of(Assert.Single(errors)));
    }

    [Fact]
    public void Options_that_need_another_driver_are_refused()
    {
        var (_, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "web", Driver = "bridge", Parent = "eth0", Attachable = true }, AllDrivers);

        Assert.Equal(["Attachable", "Parent"], errors.Select(e => e.Field).Order());
    }

    [Fact]
    public void Overlay_is_offered_only_to_swarm_managers()
    {
        string[] docker = ["bridge", "host", "ipvlan", "macvlan", "null", "overlay"];

        Assert.Equal(["bridge", "macvlan", "ipvlan"], NetworkCreateSpecBuilder.Drivers(docker, EngineCapabilities.Swarm));
        Assert.Equal(["bridge", "macvlan", "ipvlan", "overlay"], NetworkCreateSpecBuilder.Drivers(docker, EngineCapabilities.Swarm | EngineCapabilities.SwarmManager));
        Assert.Equal(["bridge", "macvlan", "ipvlan"], NetworkCreateSpecBuilder.Drivers(null, EngineCapabilities.None));
    }

    // ---------------------------------------------------------------- browsing

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("conf", "/conf")]
    [InlineData("//conf/./nginx/", "/conf/nginx")]
    [InlineData("/a/../b", null)]
    [InlineData("..", null)]
    [InlineData("/a\0b", null)]
    public void Paths_stay_inside_the_volume(string? path, string? expected) => Assert.Equal(expected, VolumePath.Normalize(path));

    [Fact]
    public void Path_helpers_walk_up_and_down()
    {
        Assert.Null(VolumePath.Parent("/"));
        Assert.Equal("/", VolumePath.Parent("/conf"));
        Assert.Equal("/conf", VolumePath.Parent("/conf/app.ini"));
        Assert.Equal("app.ini", VolumePath.Name("/conf/app.ini"));
        Assert.Equal("/conf/x", VolumePath.Combine("/conf", "x"));
        Assert.Equal([("conf", "/conf"), ("nginx", "/conf/nginx")], VolumePath.Ancestors("/conf/nginx"));
    }

    [Fact]
    public void Stat_output_becomes_entries_with_names_that_keep_their_bars()
    {
        const string output = """
            directory|4096|1795334170|755|root|root|/volume
            regular empty file|0|1795334170|666|root|root|/volume/we|ird name
            symbolic link|9|1795334170|777|root|root|/volume/link
            regular file|6|1795334170|644|1000|1000|/volume/hello.txt
            fifo|0|1795334170|644|root|root|/volume/pipe
            not a stat line
            """;

        var entries = StatOutput.Parse(output.Replace("\r", ""), "/volume").ToList();

        Assert.Equal(["/", "/we|ird name", "/link", "/hello.txt", "/pipe"], entries.Select(e => e.Path));
        Assert.Equal([VolumeEntryKind.Directory, VolumeEntryKind.File, VolumeEntryKind.Link, VolumeEntryKind.File, VolumeEntryKind.Other], entries.Select(e => e.Kind));
        var hello = entries[3];
        Assert.Equal(("hello.txt", 6L, "644", "1000", "1000"), (hello.Name, hello.Size, hello.Mode, hello.Owner, hello.Group));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795334170), hello.Modified);
    }

    // ---------------------------------------------------------------- usage

    [Fact]
    public void Volume_usage_skips_bind_mounts_and_helpers()
    {
        ContainerSummary Container(string name, IReadOnlyDictionary<string, string>? labels, params MountPoint[] mounts) =>
            new() { Id = name, Names = ["/" + name], Labels = labels, Mounts = mounts };

        var usage = VolumeUsage.ByVolume([
            Container("web", null, new MountPoint { Type = "volume", Name = "data", Destination = "/data", ReadWrite = true },
                new MountPoint { Type = "bind", Source = "/srv", Destination = "/srv" }),
            Container("backup", null, new MountPoint { Type = "volume", Name = "data", Destination = "/backup", ReadWrite = false }),
            Container("sockless-port-files-1", new Dictionary<string, string> { ["sockless-port.helper"] = VolumeBrowser.HelperKind },
                new MountPoint { Type = "volume", Name = "data", Destination = "/volume" }),
        ]);

        Assert.Equal(["data"], usage.Select(g => g.Key));
        Assert.Equal([("backup", "/backup", false), ("web", "/data", true)], usage["data"].Select(u => (u.Container.DisplayName, u.Destination, u.ReadWrite)));
    }

    [Fact]
    public void Network_usage_comes_from_the_container_list()
    {
        var usage = NetworkUsage.ByNetwork([
            new ContainerSummary { Id = "a", Names = ["/a"], NetworkSettings = new NetworkSettings { Networks = new Dictionary<string, EndpointSettings> { ["backend"] = new(), ["bridge"] = new() } } },
            new ContainerSummary { Id = "b", Names = ["/b"], NetworkSettings = new NetworkSettings { Networks = new Dictionary<string, EndpointSettings> { ["backend"] = new() } } },
        ]);

        Assert.Equal(["a", "b"], usage["backend"].Select(c => c.Id));
        Assert.Equal(["a"], usage["bridge"].Select(c => c.Id));
        Assert.Empty(usage["frontend"]);
    }

    /// <summary>A comparable view of an error (the record type is shared with containers).</summary>
    private sealed record SpecErrorView(string Field, string Message)
    {
        public static SpecErrorView Of(Sockless.Port.Containers.SpecError error) => new(error.Field, error.Message);
    }
}
