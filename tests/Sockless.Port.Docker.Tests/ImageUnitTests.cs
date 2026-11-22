using System.Net;
using System.Text;
using System.Text.Json;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

public sealed class ImageUnitTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    // ---------------------------------------------------------------- references

    [Theory]
    [InlineData("nginx", "docker.io", "library/nginx", null, null, "latest")]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27", null, "1.27")]
    [InlineData("library/nginx", "docker.io", "library/nginx", null, null, "latest")]
    [InlineData("docker.io/library/nginx:1.27", "docker.io", "library/nginx", "1.27", null, "1.27")]
    [InlineData("index.docker.io/library/nginx", "docker.io", "library/nginx", null, null, "latest")]
    [InlineData("bitnami/redis:7", "docker.io", "bitnami/redis", "7", null, "7")]
    [InlineData("ghcr.io/org/app:v1", "ghcr.io", "org/app", "v1", null, "v1")]
    [InlineData("localhost/app", "localhost", "app", null, null, "latest")]
    [InlineData("registry.local:5000/team/app:2", "registry.local:5000", "team/app", "2", null, "2")]
    [InlineData("Registry.Local:5000/app", "registry.local:5000", "app", null, null, "latest")]
    [InlineData("busybox@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "docker.io", "library/busybox", null, "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    [InlineData("ghcr.io/org/app:v1@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "ghcr.io", "org/app", "v1", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "v1")]
    public void References_are_split_and_normalised(string reference, string registry, string path, string? tag, string? digest, string? effectiveTag)
    {
        var parsed = ImageReference.Parse(reference);

        Assert.NotNull(parsed);
        Assert.Equal(registry, parsed.Registry);
        Assert.Equal(path, parsed.Path);
        Assert.Equal(tag, parsed.Tag);
        Assert.Equal(digest, parsed.Digest);
        Assert.Equal(effectiveTag, parsed.EffectiveTag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec")]
    [InlineData("a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec")]
    [InlineData("nginx:")]
    [InlineData("nginx@")]
    [InlineData("/nginx")]
    [InlineData("two words")]
    [InlineData("tag!")]
    [InlineData("Upper/Case")]
    [InlineData("app:bad tag")]
    [InlineData("app:-starts-with-dash")]
    [InlineData("app@sha256:short")]
    [InlineData("bad_host!/app")]
    public void Ids_and_nonsense_are_not_references(string reference) => Assert.Null(ImageReference.Parse(reference));

    [Fact]
    public void Every_spelling_of_a_hub_repository_is_the_same_repository()
    {
        string[] spellings = ["busybox", "library/busybox:1.37", "docker.io/library/busybox", "registry-1.docker.io/library/busybox@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"];

        Assert.Single(spellings.Select(s => ImageReference.Parse(s)!.Repository).Distinct());
    }

    [Theory]
    [InlineData("https://index.docker.io/v1/", "docker.io")]
    [InlineData("registry-1.docker.io", "docker.io")]
    [InlineData("GHCR.io", "ghcr.io")]
    [InlineData("https://registry.local:5000/v2/", "registry.local:5000")]
    public void Registry_addresses_are_normalised(string address, string expected) =>
        Assert.Equal(expected, ImageReference.NormalizeRegistry(address));

    // ---------------------------------------------------------------- export

    [Fact]
    public void The_export_platform_is_oci_json()
    {
        Assert.Equal("""{"os":"linux","architecture":"arm64","variant":"v8"}""", DockerClient.PlatformJson("linux", "arm64", "v8"));
        Assert.Equal("""{"os":"linux","architecture":"amd64"}""", DockerClient.PlatformJson("linux", "amd64", null));
        // Values are JSON-escaped, so nothing breaks out of them.
        Assert.Equal("""{"os":"li\u0022nux","architecture":"amd64"}""", DockerClient.PlatformJson("li\"nux", "amd64", ""));
    }

    // ---------------------------------------------------------------- credentials

    [Fact]
    public void Registry_auth_is_padded_base64url_json_with_lower_case_keys()
    {
        // A password chosen so the standard alphabet would need '+' and '/'.
        var header = RegistryAuth.Encode(new AuthConfig { Username = "me", Password = "??>>~~", ServerAddress = "ghcr.io" });

        Assert.DoesNotContain('+', header);
        Assert.DoesNotContain('/', header);
        Assert.Equal(0, header.Length % 4);
        using var json = JsonDocument.Parse(RegistryAuth.Decode(header));
        Assert.Equal(["username", "password", "serveraddress"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("??>>~~", json.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public void Docker_hub_credentials_use_the_index_address()
    {
        Assert.Equal("https://index.docker.io/v1/", RegistryAuth.ServerAddress("docker.io"));
        Assert.Equal("ghcr.io", RegistryAuth.ServerAddress("ghcr.io"));

        var config = RegistryAuth.EncodeConfig(new Dictionary<string, AuthConfig>
        {
            ["https://index.docker.io/v1/"] = new() { Username = "a", Password = "b", ServerAddress = "https://index.docker.io/v1/" },
        });
        using var decoded = JsonDocument.Parse(RegistryAuth.Decode(config));
        Assert.Equal("a", decoded.RootElement.GetProperty("https://index.docker.io/v1/").GetProperty("username").GetString());
    }

    // ---------------------------------------------------------------- pull progress

    private static async Task<PullTracker> TrackAsync(string fixture, Action<PullSnapshot>? each = null)
    {
        var tracker = new PullTracker();
        await using var stream = File.OpenRead(Fixture.Path(fixture));
        await foreach (var line in JsonLines.ReadAsync(stream, Json.PullProgress, TestContext.Current.CancellationToken))
        {
            tracker.Apply(line);
            each?.Invoke(tracker.Snapshot());
        }
        return tracker;
    }

    [Fact]
    public async Task Docker_pull_progress_tracks_layers_and_bytes()
    {
        var downloaded = new List<long>();
        var tracker = await TrackAsync("docker-pull.jsonl", s => downloaded.Add(s.DownloadedBytes));
        var snapshot = tracker.Snapshot();

        // The tag on the first line ("Pulling from library/python", id "3.13-slim") isn't a layer.
        Assert.DoesNotContain(snapshot.Layers, l => l.Id == "3.13-slim");
        Assert.Contains("Pulling from library/python", snapshot.Messages);
        Assert.Contains(snapshot.Messages, m => m.StartsWith("Status: ", StringComparison.Ordinal));
        // Extracted layers end "Pull complete"; config and manifest blobs stop at "Download complete".
        Assert.Equal(4, snapshot.Layers.Count(l => l.Phase == LayerPhase.Done));
        Assert.All(snapshot.Layers, l => Assert.True(l.Phase >= LayerPhase.Downloaded));
        Assert.True(snapshot.TotalBytes > 25_000_000, $"total {snapshot.TotalBytes}");
        Assert.Equal(snapshot.TotalBytes, snapshot.DownloadedBytes);
        // Extraction reported in seconds never shows up as bytes, so progress only grows.
        Assert.Equal(downloaded.Order(), downloaded);
    }

    [Fact]
    public async Task Podman_pull_progress_completes_without_a_final_status()
    {
        var tracker = await TrackAsync("podman-pull.jsonl");
        var before = tracker.Snapshot();

        Assert.NotEmpty(before.Layers);
        Assert.Empty(before.Messages);
        // Podman never says "Pull complete"; its last word per layer is "Download complete".
        Assert.All(before.Layers, l => Assert.Equal(LayerPhase.Downloaded, l.Phase));
        Assert.Equal(before.Layers.Count, before.LayersDone);

        tracker.Complete();
        Assert.All(tracker.Snapshot().Layers, l => Assert.Equal(LayerPhase.Done, l.Phase));
    }

    [Fact]
    public void Layer_phases_never_go_backwards()
    {
        var tracker = new PullTracker();
        tracker.Apply(new PullProgress { Id = "abc", Status = "Downloading", ProgressDetail = new() { Current = 50, Total = 100 } });
        tracker.Apply(new PullProgress { Id = "abc", Status = "Download complete" });
        tracker.Apply(new PullProgress { Id = "abc", Status = "Pulling fs layer" });

        var layer = Assert.Single(tracker.Snapshot().Layers);
        Assert.Equal(LayerPhase.Downloaded, layer.Phase);
        Assert.Equal(100, layer.Current);
    }

    // ---------------------------------------------------------------- models

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void Image_inspect_fixtures_deserialize(string engine)
    {
        var image = Fixture.Load($"{engine}-image-inspect.json", Json.ImageInspect);

        Assert.StartsWith("sha256:", image.Id);
        Assert.Contains(image.RepoTags!, t => t.EndsWith("fixture/app:1", StringComparison.Ordinal));
        Assert.Equal(["sh", "-c"], image.Config!.Entrypoint);
        Assert.Equal(["cat /built.txt"], image.Config.Cmd);
        Assert.Contains("8080/tcp", image.Config.ExposedPorts!.Keys);
        Assert.Equal("fixture", image.Config.Labels!["org.example.note"]);
        Assert.Equal("linux", image.Os);
        Assert.True(image.Size > 0);
    }

    [Theory]
    [InlineData("docker", 1)]
    [InlineData("podman", 2)]
    public void Pulled_images_record_their_registry_digests(string engine, int digests)
    {
        var image = Fixture.Load($"{engine}-image-inspect-pulled.json", Json.ImageInspect);
        var remote = Fixture.Load($"{engine}-distribution.json", Json.DistributionInspect);

        // Docker records the index it pulled; Podman the index and the platform manifest.
        Assert.Equal(digests, image.RepoDigests!.Count);
        Assert.Contains(image.RepoDigests!, d => d.EndsWith("@" + remote.Descriptor.Digest, StringComparison.Ordinal));
        Assert.Equal("application/vnd.oci.image.index.v1+json", remote.Descriptor.MediaType);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void History_and_lists_deserialize(string engine)
    {
        var history = Fixture.Load($"{engine}-image-history.json", Json.ListImageHistoryItem);
        var images = Fixture.Load($"{engine}-images-dangling.json", Json.ListImageSummary);

        Assert.True(history.Count >= 5);
        Assert.Contains(history, h => h.CreatedBy?.Contains("built", StringComparison.Ordinal) == true);
        Assert.Contains(images, i => i.IsDangling);
        Assert.Contains(images, i => i.Tags.Any(t => t.EndsWith("fixture/app:1", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void Delete_and_prune_fixtures_deserialize(string engine)
    {
        var untag = Fixture.Load($"{engine}-image-untag.json", Json.ListImageDeleteItem);
        var prune = Fixture.Load($"{engine}-image-prune.json", Json.ImagePruneResponse);

        Assert.EndsWith("fixture/app:2", Assert.Single(untag).Untagged);
        Assert.NotEmpty(prune.ImagesDeleted!);
        Assert.True(prune.SpaceReclaimed > 0);
    }

    // ---------------------------------------------------------------- requests

    private static FakeEngineHandler Engine(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(r => r.RequestUri!.AbsolutePath == "/version" ? FakeEngineHandler.Version("1.51") : respond(r));

    private static HttpResponseMessage Lines(string fixture) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Fixture.Read(fixture), Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(false, """{"dangling":["true"]}""")]
    [InlineData(true, """{"dangling":["false"]}""")]
    public async Task Prune_asks_for_dangling_or_all_unused(bool all, string filter)
    {
        var handler = Engine(_ => FakeEngineHandler.Json(Fixture.Read("docker-image-prune.json")));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await client.PruneImagesAsync(all, TestContext.Current.CancellationToken);

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1.51/images/prune", request.RequestUri!.AbsolutePath);
        Assert.Equal(filter, Uri.UnescapeDataString(request.RequestUri.Query["?filters=".Length..]));
    }

    [Fact]
    public async Task Tag_and_remove_send_the_engine_parameters()
    {
        var handler = Engine(r => r.Method == HttpMethod.Delete ? FakeEngineHandler.Json("""[{"Untagged":"ghcr.io/org/app:1"}]""") : new HttpResponseMessage(HttpStatusCode.Created));
        using var client = await FakeEngineHandler.ConnectAsync(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.TagImageAsync("sha256:abc", "ghcr.io/org/app", "1", ct);
        var removed = await client.RemoveImageAsync("ghcr.io/org/app:1", force: true, cancellationToken: ct);

        Assert.Equal("/v1.51/images/sha256%3Aabc/tag", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("?repo=ghcr.io%2Forg%2Fapp&tag=1", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("/v1.51/images/ghcr.io/org/app%3A1", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("?force=true", handler.Requests[2].RequestUri!.Query);
        Assert.Equal("ghcr.io/org/app:1", Assert.Single(removed).Untagged);
    }

    [Fact]
    public async Task Pull_and_distribution_carry_registry_auth()
    {
        var handler = Engine(r => r.RequestUri!.AbsolutePath.Contains("/distribution/", StringComparison.Ordinal)
            ? FakeEngineHandler.Json(Fixture.Read("docker-distribution.json"))
            : Lines("podman-pull.jsonl"));
        using var client = await FakeEngineHandler.ConnectAsync(handler);
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in client.PullImageAsync("ghcr.io/org/app:1", "HEADER", ct)) { }
        var remote = await client.InspectDistributionAsync("ghcr.io/org/app:1", "HEADER", ct);

        Assert.All(handler.Requests.Skip(1), r => Assert.Equal("HEADER", r.Headers.GetValues("X-Registry-Auth").Single()));
        Assert.Equal("/v1.51/distribution/ghcr.io/org/app%3A1/json", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.StartsWith("sha256:", remote.Descriptor.Digest);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public async Task Build_yields_output_and_the_image_id(string engine)
    {
        var handler = Engine(_ => Lines($"{engine}-build.jsonl"));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var lines = new List<BuildProgress>();
        await foreach (var line in client.BuildImageAsync(new MemoryStream([1, 2, 3]), new ImageBuildOptions { Tags = ["a:1", "a:latest"], NoCache = true },
                           "CONFIG", TestContext.Current.CancellationToken))
            lines.Add(line);

        Assert.Contains(lines, l => l.Aux?.Id?.StartsWith("sha256:", StringComparison.Ordinal) == true);
        Assert.Contains(lines, l => l.Stream?.Contains("Successfully tagged", StringComparison.Ordinal) == true);
        var request = handler.Requests[^1];
        Assert.Equal("/v1.51/build", request.RequestUri!.AbsolutePath);
        Assert.Equal("?dockerfile=Dockerfile&rm=true&forcerm=true&nocache=true&t=a%3A1&t=a%3Alatest", request.RequestUri.Query);
        Assert.Equal("application/x-tar", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("CONFIG", request.Headers.GetValues("X-Registry-Config").Single());
    }

    [Theory]
    [InlineData("docker", "returned a non-zero code: 3")]
    [InlineData("podman", "exit status 3")]
    public async Task A_failing_build_step_throws_the_engine_message(string engine, string expected)
    {
        var handler = Engine(_ => Lines($"{engine}-build-error.jsonl"));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var error = await Assert.ThrowsAsync<DockerApiException>(async () =>
        {
            await foreach (var _ in client.BuildImageAsync(new MemoryStream(), new ImageBuildOptions(), cancellationToken: TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains(expected, error.Message);
        Assert.Equal("/build", error.RequestPath);
    }
}
