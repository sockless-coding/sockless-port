using System.Collections.Concurrent;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Registries;

namespace Sockless.Port.Images;

/// <summary>Whether a tag on the engine still matches the registry. Switch on it exhaustively.</summary>
public closed record UpdateStatus(DateTimeOffset CheckedAt);

public sealed record UpToDate(DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="RemoteDigest">What the registry serves for the tag now.</param>
public sealed record UpdateAvailable(string RemoteDigest, DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="Reason">Why it couldn't be told, for people: built locally, denied, not found…</param>
public sealed record UpdateUnknown(string Reason, DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="Checked">Tags checked.</param>
/// <param name="Updates">Of those, tags with a newer image in the registry.</param>
public sealed record UpdateCheckSummary(int Checked, int Updates, int Unknown);

/// <summary>
/// Checks whether images in use have newer versions in their registry, by comparing digests
/// (no pull). The engine asks the registry, so its proxies and certificates apply. Results are
/// kept in memory per endpoint, keyed by image ID and tag, so a re-pulled image starts unchecked.
/// </summary>
public sealed class ImageUpdates(RegistryCredentials registries, TimeProvider time)
{
    private const int Parallelism = 4;

    private readonly ConcurrentDictionary<(int Endpoint, string ImageId, string Tag), UpdateStatus> _results = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastChecked = new();

    public UpdateStatus? Get(int endpointId, string imageId, string tag) => _results.GetValueOrDefault((endpointId, imageId, tag));

    /// <summary>When the last full check of the endpoint ran.</summary>
    public DateTimeOffset? LastChecked(int endpointId) => _lastChecked.TryGetValue(endpointId, out var at) ? at : null;

    /// <summary>The newest result for any tag of the image, preferring an available update.</summary>
    public UpdateStatus? ForImage(int endpointId, string imageId)
    {
        UpdateStatus? best = null;
        foreach (var ((endpoint, id, _), status) in _results)
        {
            if (endpoint != endpointId || id != imageId)
                continue;
            if (best is null || status is UpdateAvailable && best is not UpdateAvailable)
                best = status;
        }
        return best;
    }

    /// <summary>Checks every tag of every image a container uses (running or not).</summary>
    public async Task<UpdateCheckSummary> CheckInUseAsync(EngineConnection connection, DockerClient client, CancellationToken cancellationToken = default)
    {
        var containers = await client.ListContainersAsync(all: true, cancellationToken);
        var inUse = containers.Select(c => c.ImageId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var images = await client.ListImagesAsync(cancellationToken);
        var work = images.Where(i => inUse.Contains(i.Id)).SelectMany(i => i.Tags.Select(tag => (Image: i, Tag: tag))).ToList();

        var results = new ConcurrentBag<UpdateStatus>();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (item, ct) => results.Add(await CheckAsync(connection, client, item.Image.Id, item.Image.RepoDigests, item.Tag, ct)));

        _lastChecked[connection.Id] = time.GetUtcNow();
        return new UpdateCheckSummary(results.Count, results.Count(r => r is UpdateAvailable), results.Count(r => r is UpdateUnknown));
    }

    /// <summary>Checks one tag of an image and remembers the result.</summary>
    public async Task<UpdateStatus> CheckAsync(EngineConnection connection, DockerClient client, string imageId, IReadOnlyList<string>? repoDigests, string tag,
        CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        UpdateStatus status;
        if (ImageReference.Parse(tag) is null)
        {
            status = new UpdateUnknown("It isn't a registry reference.", now);
        }
        else if (LocalDigests(repoDigests, tag).Count == 0)
        {
            status = new UpdateUnknown("It has no registry digest: it was built locally or loaded from a file.", now);
        }
        else
        {
            try
            {
                var auth = await registries.AuthHeaderForAsync(tag, cancellationToken);
                var remote = await client.InspectDistributionAsync(tag, auth, cancellationToken);
                status = Compare(repoDigests, tag, remote.Descriptor.Digest, now);
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
                status = new UpdateUnknown("The registry doesn't have this tag any more.", now);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or TaskCanceledException
                                           or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException
                                       && !cancellationToken.IsCancellationRequested)
            {
                status = new UpdateUnknown(EngineErrors.ForCall(ex, connection.Address), now);
            }
        }
        _results[(connection.Id, imageId, tag)] = status;
        return status;
    }

    /// <summary>
    /// Up to date when the registry's digest is one the image is known by for that repository.
    /// Docker records the index digest it pulled; Podman records the index and the platform
    /// manifest, so either may match.
    /// </summary>
    public static UpdateStatus Compare(IReadOnlyList<string>? repoDigests, string tag, string remoteDigest, DateTimeOffset now)
    {
        var local = LocalDigests(repoDigests, tag);
        if (local.Count == 0)
            return new UpdateUnknown("It has no registry digest: it was built locally or loaded from a file.", now);
        return local.Contains(remoteDigest) ? new UpToDate(now) : new UpdateAvailable(remoteDigest, now);
    }

    /// <summary>The digests in <paramref name="repoDigests"/> (<c>repo@sha256:…</c>) that belong to the tag's repository.</summary>
    internal static HashSet<string> LocalDigests(IReadOnlyList<string>? repoDigests, string tag)
    {
        var repository = ImageReference.Parse(tag)?.Repository;
        var digests = new HashSet<string>(StringComparer.Ordinal);
        if (repository is null || repoDigests is null)
            return digests;
        foreach (var entry in repoDigests)
        {
            if (ImageReference.Parse(entry) is { Digest: { } digest } parsed && parsed.Repository == repository)
                digests.Add(digest);
        }
        return digests;
    }
}
