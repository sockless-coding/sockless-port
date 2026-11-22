using System.Formats.Tar;
using System.Net;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Tests;

/// <summary>Volumes, networks, exec output and archives against a real engine (see <see cref="EngineIntegrationTests"/>).</summary>
[Trait("Category", "Integration")]
public sealed class VolumeNetworkIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");

    /// <summary>On everything this test creates, so prunes only touch its own resources (tests run in parallel).</summary>
    private readonly string _run = $"sockless-port-test.run={Guid.NewGuid():n}";
    private readonly string _prefix = $"sp-test-{Guid.NewGuid():n}"[..20];
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];
    private readonly List<string> _networks = [];
    private DockerClient _client = null!;

    private Dictionary<string, string> Labels => new() { [_run.Split('=')[0]] = _run.Split('=')[1] };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
        if (!await _client.ImageExistsAsync(Image, TestContext.Current.CancellationToken))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: TestContext.Current.CancellationToken)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var id in _containers)
        {
            try { await _client.RemoveContainerAsync(id, force: true); } catch (DockerApiException) { }
        }
        foreach (var name in _volumes)
        {
            try { await _client.RemoveVolumeAsync(name, force: true); } catch (DockerApiException) { }
        }
        foreach (var name in _networks)
        {
            try { await _client.RemoveNetworkAsync(name); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    private async Task<string> RunAsync(JsonObject? hostConfig = null, string? network = null)
    {
        var body = new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray("sleep", "300"),
            ["Labels"] = new JsonObject { ["test"] = "sockless-port" },
            ["HostConfig"] = hostConfig ?? new JsonObject(),
        };
        if (network is not null)
            body["HostConfig"]!["NetworkMode"] = network;
        var created = await _client.CreateContainerAsync($"{_prefix}-{_containers.Count}", body, TestContext.Current.CancellationToken);
        _containers.Add(created.Id);
        await _client.StartContainerAsync(created.Id, TestContext.Current.CancellationToken);
        return created.Id;
    }

    private static JsonObject Mount(string volume, string target, bool readOnly = false) => new()
    {
        ["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume", ["Source"] = volume, ["Target"] = target, ["ReadOnly"] = readOnly }),
    };

    [Fact]
    public async Task Volume_lifecycle_with_usage_and_a_scoped_prune()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{_prefix}-data";
        var spare = $"{_prefix}-spare";
        _volumes.AddRange([name, spare]);

        var created = await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = name, Labels = Labels }, ct);
        await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = spare, Labels = Labels }, ct);
        var (inspected, json) = await _client.InspectVolumeAsync(name, ct);
        Assert.Equal(name, created.Name);
        Assert.Equal("local", inspected.Driver);
        Assert.Contains("\"Mountpoint\"", json);
        Assert.False(inspected.IsAnonymous);

        var container = await RunAsync(Mount(name, "/data"));
        var listed = await _client.ListContainersAsync(new Dictionary<string, string[]> { ["volume"] = [name] }, cancellationToken: ct);
        var mount = Assert.Single(Assert.Single(listed).Mounts!, m => m.Type == "volume");
        Assert.Equal((name, "/data"), (mount.Name, mount.Destination));

        // In use: refused, on Docker and Podman alike, as a conflict.
        var refused = await Assert.ThrowsAsync<DockerApiException>(() => _client.RemoveVolumeAsync(name, cancellationToken: ct));
        Assert.True(refused.IsConflict, refused.Message);

        // A prune scoped to this run's label removes only the unused named one.
        var pruned = await _client.PruneVolumesAsync(includeNamed: true, label: _run, cancellationToken: ct);
        Assert.Equal([spare], pruned.VolumesDeleted);

        await _client.RemoveContainerAsync(container, force: true, cancellationToken: ct);
        await _client.RemoveVolumeAsync(name, cancellationToken: ct);
        Assert.DoesNotContain(await _client.ListVolumesAsync(ct), v => v.Name == name);
    }

    [Fact]
    public async Task Anonymous_volumes_are_recognised_and_pruned_without_named_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(_client.CanPruneAnonymousVolumesOnly, "The engine prunes named volumes too.");
        var named = $"{_prefix}-keep";
        _volumes.Add(named);
        await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = named, Labels = Labels }, ct);

        // An anonymous volume, through the image-less form "-v /scratch".
        var body = new JsonObject { ["Image"] = Image, ["Cmd"] = new JsonArray("true"), ["Volumes"] = new JsonObject { ["/scratch"] = new JsonObject() } };
        var created = await _client.CreateContainerAsync($"{_prefix}-anon", body, ct);
        var (container, _) = await _client.InspectContainerAsync(created.Id, ct);
        var anonymous = Assert.Single(container.Mounts!).Name!;
        Assert.True((await _client.InspectVolumeAsync(anonymous, ct)).Volume.IsAnonymous);
        await _client.RemoveContainerAsync(created.Id, cancellationToken: ct);

        // The anonymous volume has no label of this run, so prune everything anonymous and check
        // the named one (labelled, unused) survived.
        var pruned = await _client.PruneVolumesAsync(includeNamed: false, cancellationToken: ct);
        Assert.Contains(anonymous, pruned.VolumesDeleted ?? []);
        Assert.Contains(await _client.ListVolumesAsync(ct), v => v.Name == named);
    }

    [Fact]
    public async Task Network_lifecycle_with_fixed_address_aliases_and_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{_prefix}-net";
        _networks.Add(name);
        // A random /24 in 10.200-250.x so parallel runs don't collide.
        var third = Random.Shared.Next(0, 255);
        var second = Random.Shared.Next(200, 250);
        var subnet = $"10.{second}.{third}.0/24";

        var created = await _client.CreateNetworkAsync(new NetworkCreateRequest
        {
            Name = name,
            Driver = "bridge",
            Labels = Labels,
            Ipam = new NetworkIpam { Config = [new NetworkIpamConfig { Subnet = subnet, Gateway = $"10.{second}.{third}.1", IpRange = $"10.{second}.{third}.128/25" }] },
        }, ct);
        var (network, json) = await _client.InspectNetworkAsync(created.Id, ct);
        Assert.Equal(name, network.Name);
        Assert.Contains("\"IPAM\"", json);
        Assert.Equal($"10.{second}.{third}.128/25", Assert.Single(network.Ipam!.Config!).IpRange);

        // On the default bridge explicitly: nested Podman defaults to host networking, which can't join others.
        var container = await RunAsync(network: "bridge");
        await _client.ConnectNetworkAsync(created.Id, container, new NetworkConnectOptions { Aliases = ["db"], Ipv4Address = $"10.{second}.{third}.20" }, ct);
        var connected = (await _client.InspectNetworkAsync(name, ct)).Network.Containers!;
        Assert.Equal($"10.{second}.{third}.20/24", connected[container].Ipv4Address);
        var (inspect, _) = await _client.InspectContainerAsync(container, ct);
        Assert.Contains("db", inspect.NetworkSettings!.Networks![name].Aliases!);

        var listed = await _client.ListContainersAsync(new Dictionary<string, string[]> { ["network"] = [name] }, cancellationToken: ct);
        Assert.Contains(name, Assert.Single(listed).NetworkSettings!.Networks!.Keys);

        // Connected: the engines refuse removal (Docker 403, Podman 500); both say so.
        await Assert.ThrowsAsync<DockerApiException>(() => _client.RemoveNetworkAsync(name, ct));

        await _client.DisconnectNetworkAsync(name, container, cancellationToken: ct);
        Assert.Empty((await _client.InspectNetworkAsync(name, ct)).Network.Containers ?? new Dictionary<string, NetworkContainer>());

        var pruned = await _client.PruneNetworksAsync(label: _run, cancellationToken: ct);
        Assert.Equal([name], pruned.NetworksDeleted);
        var gone = await Assert.ThrowsAsync<DockerApiException>(() => _client.InspectNetworkAsync(name, ct));
        Assert.True(gone.IsNotFound);
    }

    [Fact]
    public async Task Built_in_networks_are_flagged_and_cant_be_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var networks = await _client.ListNetworksAsync(ct);

        var bridge = Assert.Single(networks, n => n.Name == "bridge");
        Assert.True(bridge.IsPredefined);
        await Assert.ThrowsAsync<DockerApiException>(() => _client.RemoveNetworkAsync(bridge.Id, ct));
        Assert.All(networks.Where(n => n.Name.StartsWith("sp-test-", StringComparison.Ordinal)), n => Assert.False(n.IsPredefined));
    }

    [Fact]
    public async Task Exec_without_a_tty_and_archives_read_a_read_only_volume()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{_prefix}-files";
        _volumes.Add(name);
        await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = name, Labels = Labels }, ct);
        var writer = await RunAsync(Mount(name, "/data"));
        var write = await _client.RunExecAsync(writer, ["sh", "-c", "echo hello > /data/hello.txt && mkdir /data/sub && echo oops >&2 && exit 4"], cancellationToken: ct);
        Assert.Equal(("", "oops\n", (int?)4), (write.Stdout, write.Stderr, write.ExitCode));

        var reader = await RunAsync(Mount(name, "/volume", readOnly: true));
        var listing = await _client.RunExecAsync(reader, ["find", "/volume", "-maxdepth", "1", "-exec", "stat", "-c", "%F|%s|%n", "{}", "+"], cancellationToken: ct);
        Assert.Equal(0, listing.ExitCode);
        Assert.Contains("regular file|6|/volume/hello.txt", listing.Stdout);
        Assert.Contains("directory|", listing.Stdout);
        var denied = await _client.RunExecAsync(reader, ["touch", "/volume/x"], cancellationToken: ct);
        Assert.NotEqual(0, denied.ExitCode);
        Assert.Contains("Read-only", denied.Stderr);

        await using var archive = await _client.GetArchiveAsync(reader, "/volume/hello.txt", ct);
        var entry = await new TarReader(archive).GetNextEntryAsync(cancellationToken: ct);
        Assert.Equal(("hello.txt", 6L), (entry!.Name, entry.Length));
        using var text = new StreamReader(entry.DataStream!);
        Assert.Equal("hello\n", await text.ReadToEndAsync(ct));

        var missing = await Assert.ThrowsAsync<DockerApiException>(() => _client.GetArchiveAsync(reader, "/volume/nope", ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
