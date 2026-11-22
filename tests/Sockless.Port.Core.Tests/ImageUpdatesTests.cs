using System.Text.Json;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Settings;

namespace Sockless.Port.Core.Tests;

public sealed class ImageUpdatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static (List<string> RepoDigests, string Remote) Captured(string engine)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var image = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{engine}-image-inspect-pulled.json")));
        using var remote = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{engine}-distribution.json")));
        return ([.. image.RootElement.GetProperty("RepoDigests").EnumerateArray().Select(d => d.GetString()!)],
            remote.RootElement.GetProperty("Descriptor").GetProperty("digest").GetString()!);
    }

    [Theory]
    [InlineData("docker", "python:3.13-slim")]
    [InlineData("podman", "docker.io/library/python:3.13-slim")]
    public void A_freshly_pulled_image_is_up_to_date(string engine, string tag)
    {
        var (digests, remote) = Captured(engine);

        Assert.IsType<UpToDate>(ImageUpdates.Compare(digests, tag, remote, Now));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void A_new_digest_in_the_registry_is_an_update(string engine)
    {
        var (digests, _) = Captured(engine);

        var status = Assert.IsType<UpdateAvailable>(ImageUpdates.Compare(digests, "python:3.13-slim", "sha256:" + new string('f', 64), Now));
        Assert.Equal("sha256:" + new string('f', 64), status.RemoteDigest);
    }

    [Fact]
    public void Digests_of_another_repository_dont_count()
    {
        // Retagged from busybox: its digests say nothing about what registry.local/app serves.
        var digest = "sha256:" + new string('a', 64);
        List<string> digests = [$"busybox@{digest}"];

        Assert.IsType<UpdateUnknown>(ImageUpdates.Compare(digests, "registry.local/app:1", digest, Now));
        Assert.IsType<UpToDate>(ImageUpdates.Compare(digests, "docker.io/library/busybox:1.37", digest, Now));
    }

    [Fact]
    public void Images_without_registry_digests_cant_be_checked()
    {
        var status = Assert.IsType<UpdateUnknown>(ImageUpdates.Compare([], "app:1", "sha256:aaa", Now));

        Assert.Contains("built locally", status.Reason);
    }

    private static readonly string DigestA = "sha256:" + new string('a', 64);
    private static readonly string DigestB = "sha256:" + new string('b', 64);

    // Paths that never reach the engine, so no client is needed.
    private static (ImageUpdates Updates, EngineConnection Connection) Offline() =>
        (new ImageUpdates(null!, TimeProvider.System), new EngineConnection(1, "local", new UnixSocketAddress("/nonexistent/updates.sock"), EndpointKind.Local));

    [Fact]
    public void A_reference_pinned_to_a_digest_is_compared_with_that_digest()
    {
        // Swarm pins services this way, and Compose files may too; the image's own digests may be missing.
        Assert.IsType<UpToDate>(ImageUpdates.Compare(null, $"nginx:1.27@{DigestA}", DigestA, Now));
        var update = Assert.IsType<UpdateAvailable>(ImageUpdates.Compare(null, $"nginx:1.27@{DigestA}", DigestB, Now));
        Assert.Equal(DigestB, update.RemoteDigest);
    }

    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx:latest")]
    [InlineData("library/nginx:latest")]
    [InlineData("docker.io/library/nginx:latest")]
    public void Every_spelling_of_a_reference_finds_the_same_result(string spelling) =>
        Assert.Equal("docker.io/library/nginx:latest", ImageUpdates.Key(spelling));

    [Fact]
    public async Task A_container_finds_the_result_for_its_own_reference_first()
    {
        var (updates, connection) = Offline();
        // No registry digests, so both are told without asking the registry.
        await updates.CheckAsync(connection, null!, "sha256:img", [], "app:1", TestContext.Current.CancellationToken);
        await updates.CheckAsync(connection, null!, "sha256:img", null, "sha256:img", TestContext.Current.CancellationToken);

        var own = Assert.IsType<UpdateUnknown>(updates.ForContainer(1, "sha256:img", "app:1"));
        Assert.Contains("built locally", own.Reason);
        // Docker lists a container by image ID once its tag moved on: any result for the image counts then.
        Assert.NotNull(updates.ForContainer(1, "sha256:img", "sha256:img"));
        Assert.Null(updates.ForContainer(1, "sha256:other", "app:1"));
        Assert.Null(updates.ForContainer(2, "sha256:img", "app:1"));
    }

    [Fact]
    public async Task A_digest_without_a_tag_has_nothing_newer()
    {
        var (updates, connection) = Offline();

        var status = await updates.CheckAsync(connection, null!, "sha256:img", null, $"nginx@{DigestA}", TestContext.Current.CancellationToken);

        Assert.Contains("pinned to a digest", Assert.IsType<UpdateUnknown>(status).Reason);
    }

    [Fact]
    public async Task Services_that_arent_pinned_say_why_they_cant_be_checked()
    {
        var (updates, connection) = Offline();
        var service = new SwarmService
        {
            Id = "svc",
            Spec = new ServiceSpec { Name = "web", TaskTemplate = new TaskSpec { ContainerSpec = new ContainerTaskSpec { Image = "nginx:1.27" } } },
        };

        var summary = await updates.CheckServicesAsync(connection, null!, [service], TestContext.Current.CancellationToken);

        Assert.Equal(new UpdateCheckSummary(1, 0, 1), summary);
        Assert.Contains("isn't pinned", Assert.IsType<UpdateUnknown>(updates.ForService(1, service)).Reason);
        Assert.Null(updates.ForService(1, $"nginx:1.27@{DigestA}"));
    }

    [Fact]
    public void Summaries_count_only_what_was_checked()
    {
        var summary = UpdateCheckSummary.Of([new UpToDate(Now), new UpdateAvailable(DigestA, Now), new UpdateUnknown("x", Now), null]);

        Assert.Equal(new UpdateCheckSummary(3, 1, 1), summary);
        Assert.Equal(1, summary.UpToDate);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(24, true)]
    [InlineData(168, true)]
    [InlineData(5, false)]
    [InlineData(-1, false)]
    public void The_scheduled_check_takes_only_the_offered_intervals(int hours, bool valid) =>
        Assert.Equal(valid, (AppSettings.Default with { ImageUpdateCheckHours = hours }).Problems().Count == 0);
}
