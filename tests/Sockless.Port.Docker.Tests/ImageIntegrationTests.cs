using System.Formats.Tar;
using System.Text;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

/// <summary>Pull, inspect, tag, build and remove against a real engine (see <see cref="EngineIntegrationTests"/>).</summary>
[Trait("Category", "Integration")]
public sealed class ImageIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");

    private readonly string _repository = $"sockless-port-test/{Guid.NewGuid():n}"[..32];
    private DockerClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var tag in new[] { "1", "2", "built", "built-again" })
        {
            try { await _client.RemoveImageAsync($"{_repository}:{tag}", force: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    private static MemoryStream Context(string dockerfile)
    {
        var stream = new MemoryStream();
        using (var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(dockerfile)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            });
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Pull_reports_progress_and_the_registry_digest_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = new PullTracker();
        await foreach (var line in _client.PullImageAsync(Image, cancellationToken: ct))
            tracker.Apply(line);
        tracker.Complete();

        var (image, json) = await _client.InspectImageAsync(Image, ct);
        Assert.Contains("\"RepoTags\"", json);
        Assert.All(tracker.Snapshot().Layers, l => Assert.Equal(LayerPhase.Done, l.Phase));

        if (!EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(ct), _client.ApiVersion).Has(EngineCapabilities.RegistryDigests))
        {
            // Podman before 6.2 has no distribution endpoint.
            await Assert.ThrowsAsync<DockerApiException>(() => _client.InspectDistributionAsync(Image, cancellationToken: ct));
            return;
        }
        var remote = await _client.InspectDistributionAsync(Image, cancellationToken: ct);
        // Docker records the index digest, Podman the index and the platform manifest: the registry's digest is among them.
        Assert.Contains(image.RepoDigests!, d => d.EndsWith("@" + remote.Descriptor.Digest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_image_exports_as_a_tarball_with_a_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        var (image, _) = await _client.InspectImageAsync(Image, ct);

        // The platform the engine has: Docker's containerd store may hold only that one of a multi-platform image.
        await using var export = await _client.ExportImageAsync(image.Id, image.Os, image.Architecture, image.Variant, ct);
        var names = new List<string>();
        await using (var tar = new TarReader(export))
        {
            while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                names.Add(entry.Name);
        }

        // docker save (and Podman's docker-archive) always has manifest.json: what the scanner reads.
        Assert.Contains("manifest.json", names);
    }

    [Fact]
    public async Task Tag_history_and_untag()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
        var (source, _) = await _client.InspectImageAsync(Image, ct);

        await _client.TagImageAsync(source.Id, _repository, "1", ct);
        await _client.TagImageAsync(source.Id, _repository, "2", ct);
        var (tagged, _) = await _client.InspectImageAsync($"{_repository}:1", ct);
        var history = await _client.GetImageHistoryAsync($"{_repository}:1", ct);
        var untagged = await _client.RemoveImageAsync($"{_repository}:2", cancellationToken: ct);

        Assert.Equal(source.Id, tagged.Id);
        Assert.Contains(tagged.RepoTags!, t => t.EndsWith($"{_repository}:2", StringComparison.Ordinal));
        Assert.NotEmpty(history);
        // Another tag still points at the image, so only the tag goes.
        Assert.EndsWith($"{_repository}:2", Assert.Single(untagged).Untagged);
        Assert.True(await _client.ImageExistsAsync(Image, ct));
    }

    [Fact]
    public async Task Build_streams_output_and_a_rebuild_leaves_the_old_image_dangling()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = $"{_repository}:built";
        const string dockerfile = "FROM busybox:1.37\nLABEL org.example.test=sockless-port\nRUN echo {0} > /marker\nCMD [\"cat\", \"/marker\"]\n";

        async Task<string> BuildAsync(string marker)
        {
            string? id = null;
            var output = new StringBuilder();
            await foreach (var line in _client.BuildImageAsync(Context(string.Format(System.Globalization.CultureInfo.InvariantCulture, dockerfile, marker)),
                               new ImageBuildOptions { Tags = [tag] }, cancellationToken: ct))
            {
                output.Append(line.Stream);
                id = line.Aux?.Id ?? id;
            }
            Assert.Contains("Successfully tagged", output.ToString());
            return id ?? throw new InvalidDataException("No image ID in the build output.");
        }

        var first = await BuildAsync("one");
        var second = await BuildAsync("two");
        var (built, _) = await _client.InspectImageAsync(tag, ct);
        var dangling = (await _client.ListImagesAsync(ct)).Where(i => i.Id == first).ToList();
        // Not pruned here: on Docker's containerd store a prune collects the leases of pulls in
        // progress, which breaks tests pulling in parallel. Removing it by ID is what prune does to it.
        var removed = await _client.RemoveImageAsync(first, cancellationToken: ct);

        Assert.NotEqual(first, second);
        Assert.Equal(second, built.Id);
        Assert.Equal("sockless-port", built.Config!.Labels!["org.example.test"]);
        Assert.True(Assert.Single(dangling).IsDangling);
        Assert.Contains(removed, d => d.Deleted is { } deleted && first.EndsWith(deleted.Replace("sha256:", ""), StringComparison.Ordinal));
        Assert.DoesNotContain(await _client.ListImagesAsync(ct), i => i.Id == first);
    }

    [Fact]
    public async Task A_failing_build_throws_and_leaves_no_container()
    {
        var ct = TestContext.Current.CancellationToken;
        // Other tests may create containers meanwhile: count only build containers of this step.
        var marker = $"exit 3 # {Guid.NewGuid():n}";
        async Task<int> LeftoversAsync() => (await _client.ListContainersAsync(all: true, ct)).Count(c => c.Command?.Contains(marker, StringComparison.Ordinal) == true);

        var error = await Assert.ThrowsAsync<DockerApiException>(async () =>
        {
            await foreach (var _ in _client.BuildImageAsync(Context("FROM busybox:1.37\nRUN exit 3\n"), new ImageBuildOptions(), cancellationToken: ct)) { }
        });

        Assert.Contains("3", error.Message);
        Assert.Equal(0, await LeftoversAsync());
    }

    [Fact]
    public async Task Removing_an_image_in_use_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
        var name = $"sockless-port-test-{Guid.NewGuid():n}"[..32];
        var created = await _client.CreateContainerAsync(name, new System.Text.Json.Nodes.JsonObject { ["Image"] = Image, ["Cmd"] = new System.Text.Json.Nodes.JsonArray("true") }, ct);
        try
        {
            var (image, _) = await _client.InspectImageAsync(Image, ct);
            var error = await Assert.ThrowsAsync<DockerApiException>(() => _client.RemoveImageAsync(image.Id, cancellationToken: ct));
            Assert.True(error.IsConflict, $"{error.StatusCode}: {error.Message}");
        }
        finally
        {
            await _client.RemoveContainerAsync(created.Id, force: true, cancellationToken: ct);
        }
    }

    [Fact]
    public async Task Wrong_registry_credentials_are_rejected()
    {
        var error = await Assert.ThrowsAsync<DockerApiException>(() => _client.CheckAuthAsync(
            new AuthConfig { Username = "sockless-port-nobody", Password = "wrong", ServerAddress = RegistryAuth.DockerHubServerAddress },
            TestContext.Current.CancellationToken));

        // Docker: "unauthorized: incorrect username or password"; Podman: "… failed with status: 401 Unauthorized".
        Assert.Matches("(?i)unauthorized|401", error.Message);
    }
}
