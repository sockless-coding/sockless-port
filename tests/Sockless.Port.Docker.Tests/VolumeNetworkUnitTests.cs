using System.Formats.Tar;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

/// <summary>Volume and network models against captured responses, and the requests the client sends.</summary>
public sealed class VolumeNetworkUnitTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    /// <summary>Request bodies by request, read before the client disposes them.</summary>
    private readonly Dictionary<HttpRequestMessage, string> _bodies = [];

    private FakeEngineHandler Engine(string apiVersion, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(r =>
        {
            if (r.Content is { } content)
                _bodies[r] = content.ReadAsStringAsync().GetAwaiter().GetResult();
            return r.RequestUri!.AbsolutePath == "/version" ? FakeEngineHandler.Version(apiVersion) : respond(r);
        });

    private Task<string> BodyAsync(HttpRequestMessage request) => Task.FromResult(_bodies[request]);

    // ---------------------------------------------------------------- volumes

    [Theory]
    [InlineData("docker-volumes-list.json")]
    [InlineData("podman-volumes-list.json")]
    public void Anonymous_volumes_are_told_apart_on_both_engines(string fixture)
    {
        // Docker labels anonymous volumes; Podman doesn't, but both generate 64-hex names.
        var volumes = Fixture.Load(fixture, Json.VolumeListResponse).Volumes!;

        Assert.Equal(["fixture-data", "fixture-unused"], volumes.Where(v => !v.IsAnonymous).Select(v => v.Name).Order());
        Assert.Single(volumes, v => v.IsAnonymous);
    }

    [Theory]
    [InlineData("docker-volume-inspect.json", "/var/lib/docker/volumes/fixture-data/_data")]
    [InlineData("podman-volume-inspect.json", "/var/lib/containers/storage/volumes/fixture-data/_data")]
    public void Volume_inspect_is_read(string fixture, string mountpoint)
    {
        var volume = Fixture.Load(fixture, Json.VolumeSummary);

        Assert.Equal("fixture-data", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.Equal(mountpoint, volume.Mountpoint);
        Assert.Equal("infra", volume.Labels!["team"]);
        Assert.False(volume.IsAnonymous);
    }

    [Theory]
    [InlineData("docker-containers-mounts.json")]
    [InlineData("podman-containers-mounts.json")]
    public void Container_list_carries_volume_mounts_and_networks(string fixture)
    {
        var containers = Fixture.Load(fixture, Json.ListContainerSummary);
        var web = Assert.Single(containers, c => c.DisplayName == "fixture-web");

        var mount = Assert.Single(web.Mounts!);
        Assert.Equal(("volume", "fixture-data", "/data", true), (mount.Type, mount.Name, mount.Destination, mount.ReadWrite));
        Assert.Equal(["fixture-net"], web.NetworkSettings!.Networks!.Keys);
        Assert.True(web.CanJoinNetworks);
    }

    [Fact]
    public void Containers_on_the_host_network_cant_join_others()
    {
        // Nested Podman runs fixture-anon with host networking.
        var anon = Assert.Single(Fixture.Load("podman-containers-mounts.json", Json.ListContainerSummary), c => c.DisplayName == "fixture-anon");

        Assert.False(anon.CanJoinNetworks);
        Assert.False(new ContainerSummary { Id = "x", HostConfig = new HostConfig { NetworkMode = "container:db" } }.CanJoinNetworks);
        Assert.True(new ContainerSummary { Id = "x" }.CanJoinNetworks);
    }

    [Fact]
    public async Task Prune_keeps_named_volumes_unless_asked_and_says_so_on_old_engines()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = Engine("1.51", _ => FakeEngineHandler.Json("""{"VolumesDeleted":["a"],"SpaceReclaimed":42}"""));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var anonymous = await client.PruneVolumesAsync(includeNamed: false, cancellationToken: ct);
        await client.PruneVolumesAsync(includeNamed: true, cancellationToken: ct);

        Assert.True(client.CanPruneAnonymousVolumesOnly);
        Assert.Equal(["a"], anonymous.VolumesDeleted);
        Assert.Equal(42, anonymous.SpaceReclaimed);
        Assert.Equal("/v1.51/volumes/prune", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal("/v1.51/volumes/prune?filters=" + Uri.EscapeDataString("""{"all":["true"]}"""), handler.Requests[2].RequestUri!.PathAndQuery);

        // Below 1.42 a plain prune removes named volumes too, and "all" is an unknown filter.
        var old = Engine("1.41", _ => FakeEngineHandler.Json("""{"VolumesDeleted":[]}"""));
        using var oldClient = await FakeEngineHandler.ConnectAsync(old);
        Assert.False(oldClient.CanPruneAnonymousVolumesOnly);
        await Assert.ThrowsAsync<NotSupportedException>(() => oldClient.PruneVolumesAsync(includeNamed: false, cancellationToken: ct));
        await oldClient.PruneVolumesAsync(includeNamed: true, cancellationToken: ct);
        Assert.Equal("/v1.41/volumes/prune", old.Requests[^1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Volume_sizes_ask_for_volumes_only()
    {
        var handler = Engine("1.51", _ => FakeEngineHandler.Json("""{"Volumes":[{"Name":"data","UsageData":{"RefCount":1,"Size":3015}}]}"""));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var usage = Assert.Single(await client.GetVolumeUsageAsync(TestContext.Current.CancellationToken));

        Assert.Equal(("data", 3015L), (usage.Name, usage.UsageData!.Size));
        Assert.Equal("/v1.51/system/df?type=volume", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    // ---------------------------------------------------------------- networks

    [Theory]
    [InlineData("docker-network-inspect.json", "com.docker.network.driver.mtu")]
    [InlineData("podman-network-inspect.json", "mtu")]
    public void Network_inspect_has_addresses_and_containers(string fixture, string mtuOption)
    {
        // Podman translates Docker's option names to its own.
        var network = Fixture.Load(fixture, Json.NetworkSummary);

        Assert.Equal("fixture-net", network.Name);
        Assert.False(network.IsPredefined);
        var config = Assert.Single(network.Ipam!.Config!);
        Assert.Equal(("10.77.0.0/24", "10.77.0.1", "10.77.0.128/25"), (config.Subnet, config.Gateway, config.IpRange));
        Assert.Equal("1400", network.Options![mtuOption]);
        var container = Assert.Single(network.Containers!.Values);
        Assert.Equal("fixture-web", container.Name);
        Assert.StartsWith("10.77.0.", container.Ipv4Address);
    }

    [Theory]
    [InlineData("docker-networks-list.json", new[] { "bridge", "host", "none" })]
    [InlineData("podman-networks-list.json", new[] { "bridge" })]
    [InlineData("swarm-networks.json", new[] { "bridge", "docker_gwbridge", "host", "ingress", "none" })]
    public void Built_in_networks_are_recognised(string fixture, string[] builtIn)
    {
        var networks = Fixture.Load(fixture, Json.ListNetworkSummary);

        Assert.Equal(builtIn, networks.Where(n => n.IsPredefined).Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.All(networks.Where(n => n.Name.StartsWith("fixture-", StringComparison.Ordinal)), n => Assert.False(n.IsPredefined));
    }

    [Fact]
    public async Task Network_create_uses_the_engine_field_names()
    {
        var handler = Engine("1.51", _ => FakeEngineHandler.Json("""{"Id":"abc","Warning":""}""", HttpStatusCode.Created));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var created = await client.CreateNetworkAsync(new NetworkCreateRequest
        {
            Name = "backend",
            Driver = "bridge",
            EnableIpv6 = true,
            Ipam = new NetworkIpam { Config = [new NetworkIpamConfig { Subnet = "10.1.0.0/24", IpRange = "10.1.0.128/25" }] },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("abc", created.Id);
        var body = JsonNode.Parse(await BodyAsync(handler.Requests[1]))!;
        Assert.True(body["EnableIPv6"]!.GetValue<bool>());
        Assert.Equal("10.1.0.128/25", body["IPAM"]!["Config"]![0]!["IPRange"]!.GetValue<string>());
        Assert.Null(body["Options"]);
    }

    [Fact]
    public async Task Connect_and_disconnect_send_endpoint_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = Engine("1.51", _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await client.ConnectNetworkAsync("net", "c1", new NetworkConnectOptions { Aliases = ["db"], Ipv4Address = "10.1.0.5" }, ct);
        await client.ConnectNetworkAsync("net", "c2", new NetworkConnectOptions(), ct);
        await client.DisconnectNetworkAsync("net", "c1", force: true, ct);

        Assert.Equal("""{"Container":"c1","EndpointConfig":{"Aliases":["db"],"IPAMConfig":{"IPv4Address":"10.1.0.5"}}}""", await BodyAsync(handler.Requests[1]));
        Assert.Equal("""{"Container":"c2"}""", await BodyAsync(handler.Requests[2]));
        Assert.Equal("/v1.51/networks/net/disconnect", handler.Requests[3].RequestUri!.AbsolutePath);
        Assert.Equal("""{"Container":"c1","Force":true}""", await BodyAsync(handler.Requests[3]));
    }

    [Theory]
    [InlineData("docker-info.json", new[] { "bridge", "host", "ipvlan", "macvlan", "null", "overlay" })]
    [InlineData("podman-info.json", new[] { "bridge", "macvlan", "ipvlan" })]
    public void Info_lists_network_drivers(string fixture, string[] drivers) =>
        Assert.Equal(drivers, Fixture.Load(fixture, Json.SystemInfo).Plugins!.Network);

    // ---------------------------------------------------------------- exec and archive

    private static byte[] Frame(StreamKind kind, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)kind;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static HttpResponseMessage Exec(HttpRequestMessage request, byte[] output, string inspect) => request.RequestUri!.AbsolutePath switch
    {
        var p when p.EndsWith("/exec", StringComparison.Ordinal) => FakeEngineHandler.Json("""{"Id":"e1"}""", HttpStatusCode.Created),
        var p when p.EndsWith("/start", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output) },
        _ => FakeEngineHandler.Json(inspect),
    };

    [Fact]
    public async Task Exec_collects_stdout_and_stderr_and_the_exit_code()
    {
        byte[] output = [.. Frame(StreamKind.Stdout, "one\n"), .. Frame(StreamKind.Stderr, "warn\n"), .. Frame(StreamKind.Stdout, "two\n")];
        var handler = Engine("1.51", r => Exec(r, output, """{"Running":false,"ExitCode":3}"""));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var result = await client.RunExecAsync("c1", ["ls", "/"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExecResult("one\ntwo\n", "warn\n", 3, Truncated: false), result);
        var create = JsonNode.Parse(await BodyAsync(handler.Requests[1]))!;
        Assert.False(create["Tty"]!.GetValue<bool>());
        Assert.False(create["AttachStdin"]!.GetValue<bool>());
        Assert.Equal("""{"Detach":false,"Tty":false}""", await BodyAsync(handler.Requests[2]));
    }

    [Fact]
    public async Task Exec_output_beyond_the_limit_is_cut_and_marked()
    {
        byte[] output = [.. Frame(StreamKind.Stdout, "0123456789"), .. Frame(StreamKind.Stdout, "abcdef")];
        var handler = Engine("1.51", r => Exec(r, output, """{"Running":true}"""));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var result = await client.RunExecAsync("c1", ["cat"], maxOutputBytes: 12, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExecResult("0123456789ab", "", null, Truncated: true), result);
        // No exit code is waited for: the command may still be running.
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Archive_streams_the_tar_body()
    {
        var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "hello.txt") { DataStream = new MemoryStream("hi"u8.ToArray()) },
                TestContext.Current.CancellationToken);
        }
        var handler = Engine("1.51", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tar.ToArray()) });
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await using var archive = await client.GetArchiveAsync("c1", "/volume/hello.txt", TestContext.Current.CancellationToken);
        var entry = await new TarReader(archive).GetNextEntryAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello.txt", entry!.Name);
        Assert.Equal("/v1.51/containers/c1/archive?path=%2Fvolume%2Fhello.txt", handler.Requests[1].RequestUri!.PathAndQuery);
    }
}
