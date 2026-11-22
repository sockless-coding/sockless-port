using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Tests;

public sealed class EngineDetectionTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    private static EngineDescriptor Detect(string prefix)
    {
        var version = Fixture.Load($"{prefix}-version.json", Json.VersionResponse);
        var info = Fixture.Load($"{prefix}-info.json", Json.SystemInfo);
        return EngineDescriptor.Detect(version, info, DockerClient.Negotiate(version));
    }

    [Fact]
    public void Plain_docker_has_no_extra_capabilities()
    {
        var engine = Detect("docker");

        Assert.Equal(EngineKind.Docker, engine.Kind);
        Assert.Equal(EngineCapabilities.None, engine.Capabilities);
        Assert.Equal("Docker 29.10.0", engine.DisplayName);
        Assert.Equal("linux/amd64", engine.Platform);
        Assert.Equal(DockerClient.MaximumApiVersion, engine.ApiVersion);
    }

    [Fact]
    public void Swarm_manager_is_detected()
    {
        var engine = Detect("swarm-manager");

        Assert.Equal(EngineKind.Docker, engine.Kind);
        Assert.True(engine.Has(EngineCapabilities.Swarm));
        Assert.True(engine.Has(EngineCapabilities.SwarmManager));
    }

    [Fact]
    public void Swarm_worker_is_not_a_manager()
    {
        var version = Fixture.Load("swarm-manager-version.json", Json.VersionResponse);
        var info = Fixture.Load("swarm-manager-info.json", Json.SystemInfo);
        var worker = info with { Swarm = info.Swarm! with { ControlAvailable = false, Nodes = null, Managers = null } };

        var engine = EngineDescriptor.Detect(version, worker, DockerClient.Negotiate(version));

        Assert.True(engine.Has(EngineCapabilities.Swarm));
        Assert.False(engine.Has(EngineCapabilities.SwarmManager));
    }

    [Fact]
    public void Rootless_engines_are_detected_from_their_security_options()
    {
        var version = Fixture.Load("podman-version.json", Json.VersionResponse);
        var info = Fixture.Load("podman-info.json", Json.SystemInfo);
        var rootless = info with { SecurityOptions = ["name=seccomp,profile=default", "name=rootless"] };

        Assert.False(EngineDescriptor.Detect(version, info, DockerClient.Negotiate(version)).Has(EngineCapabilities.Rootless));
        Assert.True(EngineDescriptor.Detect(version, rootless, DockerClient.Negotiate(version)).Has(EngineCapabilities.Rootless));
    }

    [Fact]
    public void Rootless_on_cgroups_v1_is_told_apart()
    {
        var version = Fixture.Load("podman-version.json", Json.VersionResponse);
        var info = Fixture.Load("podman-info.json", Json.SystemInfo);
        var rootless = info with { SecurityOptions = ["name=rootless"] };

        Assert.Equal(2, EngineDescriptor.Detect(version, info, DockerClient.Negotiate(version)).CgroupVersion);
        Assert.False(EngineDescriptor.Detect(version, rootless, DockerClient.Negotiate(version)).RootlessWithoutCgroups);
        Assert.True(EngineDescriptor.Detect(version, rootless with { CgroupVersion = "1" }, DockerClient.Negotiate(version)).RootlessWithoutCgroups);
        // Rootful on v1 still has cgroups.
        Assert.False(EngineDescriptor.Detect(version, info with { CgroupVersion = "1" }, DockerClient.Negotiate(version)).RootlessWithoutCgroups);
        Assert.Null(EngineDescriptor.Detect(version, info with { CgroupVersion = null }, DockerClient.Negotiate(version)).CgroupVersion);
    }

    [Fact]
    public void Podman_is_detected_from_its_engine_component()
    {
        var engine = Detect("podman");

        Assert.Equal(EngineKind.Podman, engine.Kind);
        Assert.Equal("Podman 6.2.0", engine.DisplayName);
        Assert.Equal(EngineCapabilities.Pods, engine.Capabilities);
        Assert.Equal(new Version(1, 44), engine.ApiVersion);
    }

    [Fact]
    public void Docker_desktop_is_docker()
    {
        var version = Fixture.Load("docker-desktop-version.json", Json.VersionResponse);

        var engine = EngineDescriptor.Detect(version, new SystemInfo(), DockerClient.Negotiate(version));

        Assert.Equal(EngineKind.Docker, engine.Kind);
    }

    [Fact]
    public void Captured_info_and_disk_usage_deserialize()
    {
        var info = Fixture.Load("docker-info.json", Json.SystemInfo);
        Assert.True(info.NCpu > 0);
        Assert.True(info.MemTotal > 0);
        Assert.Equal(2, info.Containers);
        Assert.Equal(1, info.ContainersRunning);
        Assert.Equal("inactive", info.Swarm?.LocalNodeState);

        var df = Fixture.Load("docker-df.json", Json.DiskUsage);
        Assert.Single(df.Images!);
        Assert.Equal(2, df.Containers!.Count);
        Assert.Contains(df.Volumes!, v => v.Name == "fixture-data" && v.UsageData!.RefCount == 1);

        var podmanDf = Fixture.Load("podman-df.json", Json.DiskUsage);
        Assert.NotNull(podmanDf);
    }

    [Fact]
    public void Captured_resource_lists_deserialize()
    {
        var containers = Fixture.Load("docker-containers.json", Json.ListContainerSummary);
        Assert.Contains(containers, c => c.Names!.Contains("/fixture-web") && c.State == "running");
        Assert.Contains(containers, c => c.Names!.Contains("/fixture-done") && c.State == "exited");

        Assert.Contains(Fixture.Load("docker-volumes.json", Json.VolumeListResponse).Volumes!, v => v.Name == "fixture-data");
        Assert.Contains(Fixture.Load("docker-networks.json", Json.ListNetworkSummary), n => n is { Name: "fixture-net", Driver: "bridge" });
        Assert.Contains(Fixture.Load("docker-images.json", Json.ListImageSummary), i => i.RepoTags!.Contains("busybox:1.37"));
    }
}
