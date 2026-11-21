using System.Text.Json;
using Sockless.Port.Images;

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
}
