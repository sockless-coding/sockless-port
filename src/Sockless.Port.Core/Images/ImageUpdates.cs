using System.Collections.Concurrent;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Work;

namespace Sockless.Port.Images;

/// <summary>Whether a tag on the engine still matches the registry. Switch on it exhaustively.</summary>
public closed record UpdateStatus(DateTimeOffset CheckedAt);

public sealed record UpToDate(DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="RemoteDigest">What the registry serves for the tag now.</param>
public sealed record UpdateAvailable(string RemoteDigest, DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="Reason">Why it couldn't be told, for people: built locally, denied, not found…</param>
public sealed record UpdateUnknown(string Reason, DateTimeOffset CheckedAt) : UpdateStatus(CheckedAt);

/// <param name="Checked">What was checked: tags, containers or services, as the caller asked.</param>
/// <param name="Updates">Of those, the ones with a newer image in the registry.</param>
public sealed record UpdateCheckSummary(int Checked, int Updates, int Unknown)
{
    public int UpToDate => Checked - Updates - Unknown;

    public static UpdateCheckSummary Of(IEnumerable<UpdateStatus?> statuses)
    {
        var list = statuses.OfType<UpdateStatus>().ToList();
        return new(list.Count, list.Count(s => s is UpdateAvailable), list.Count(s => s is UpdateUnknown));
    }
}

/// <summary>
/// Checks whether images in use have newer versions in their registry, by comparing digests
/// (no pull). The engine asks the registry, so its proxies and certificates apply. Results are
/// kept in memory per endpoint, keyed by image ID and reference, so a re-pulled image starts
/// unchecked. Swarm services are keyed by the digest their spec pins instead, since the image
/// may not be on the manager at all.
/// </summary>
public sealed class ImageUpdates(RegistryCredentials registries, TimeProvider time, WorkTracker? work = null)
{
    private const int Parallelism = 4;

    /// <summary>Subject is an image ID, or for a swarm service the digest it's pinned to ("" when it isn't).</summary>
    private readonly ConcurrentDictionary<(int Endpoint, string Subject, string Reference), UpdateStatus> _results = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastChecked = new();

    public UpdateStatus? Get(int endpointId, string imageId, string tag) => _results.GetValueOrDefault((endpointId, imageId, Key(tag)));

    /// <summary>The references on the endpoint whose registry has a newer image, as last checked.</summary>
    public IReadOnlyList<string> Available(int endpointId) =>
        [.. _results.Where(r => r.Key.Endpoint == endpointId && r.Value is UpdateAvailable).Select(r => r.Key.Reference).Distinct(StringComparer.Ordinal)];

    /// <summary>When the last full check of the endpoint ran, from a page or on schedule.</summary>
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

    /// <summary>
    /// The result for what a container runs: its own reference when that was checked, else any
    /// tag of its image. Docker lists a container whose tag has since moved to another image by
    /// the image ID, so <paramref name="reference"/> may not be a reference at all.
    /// </summary>
    public UpdateStatus? ForContainer(int endpointId, string? imageId, string? reference)
    {
        if (imageId is null)
            return null;
        return (ImageReference.Parse(reference) is not null ? Get(endpointId, imageId, reference!) : null) ?? ForImage(endpointId, imageId);
    }

    public UpdateStatus? ForContainer(int endpointId, ContainerSummary container) => ForContainer(endpointId, container.ImageId, container.Image);

    /// <param name="specImage">The image in the service's spec, usually pinned: <c>nginx:1.27@sha256:…</c>.</param>
    public UpdateStatus? ForService(int endpointId, string? specImage) =>
        specImage is null ? null : _results.GetValueOrDefault((endpointId, ServiceSubject(specImage), Key(specImage)));

    public UpdateStatus? ForService(int endpointId, SwarmService service) => ForService(endpointId, service.Spec?.TaskTemplate?.ContainerSpec?.Image);

    /// <summary>
    /// Checks every tag of every image a container uses (running or not), and the reference each
    /// container was created from. Counts tags, for the images page.
    /// </summary>
    public Task<UpdateCheckSummary> CheckInUseAsync(EngineConnection connection, DockerClient client, CancellationToken cancellationToken = default) =>
        TrackAsync(connection, "Checking images in use for updates", item => CheckInUseAsync(connection, client, item, cancellationToken),
            s => Outcome(s, "tag", "tags"));

    private async Task<UpdateCheckSummary> CheckInUseAsync(EngineConnection connection, DockerClient client, WorkItem? item, CancellationToken cancellationToken)
    {
        var containers = await client.ListContainersAsync(all: true, cancellationToken);
        var results = await CheckAsync(connection, client, containers, allTags: true, item, cancellationToken);
        _lastChecked[connection.Id] = time.GetUtcNow();
        return UpdateCheckSummary.Of(results);
    }

    /// <summary>
    /// Checks what the given containers run: the reference each was created from, or its image's
    /// tags when it was created from an ID. Counts containers.
    /// </summary>
    public Task<UpdateCheckSummary> CheckContainersAsync(EngineConnection connection, DockerClient client, IReadOnlyList<ContainerSummary> containers,
        CancellationToken cancellationToken = default) =>
        TrackAsync(connection, "Checking containers for updates", async item =>
        {
            await CheckAsync(connection, client, containers, allTags: false, item, cancellationToken);
            return UpdateCheckSummary.Of(containers.Select(c => ForContainer(connection.Id, c)));
        }, s => Outcome(s, "container", "containers"));

    /// <summary>Checks one container, by its ID. Returns its result.</summary>
    public async Task<UpdateStatus?> CheckContainerAsync(EngineConnection connection, DockerClient client, string containerId, CancellationToken cancellationToken = default)
    {
        var (container, _) = await client.InspectContainerAsync(containerId, cancellationToken);
        if (container.Image is not { } imageId)
            return null;
        var (image, _) = await client.InspectImageAsync(imageId, cancellationToken);
        var reference = container.Config?.Image;
        var tags = ImageReference.Parse(reference) is not null ? [reference!] : image.RepoTags?.Where(t => t != "<none>:<none>").ToList() ?? [];
        foreach (var tag in tags)
            await CheckAsync(connection, client, imageId, image.RepoDigests, tag, cancellationToken);
        return ForContainer(connection.Id, imageId, reference);
    }

    /// <summary>
    /// Checks swarm services: whether the registry still serves the digest each one's spec is
    /// pinned to. Counts services.
    /// </summary>
    public Task<UpdateCheckSummary> CheckServicesAsync(EngineConnection connection, DockerClient client, IReadOnlyList<SwarmService> services,
        CancellationToken cancellationToken = default) =>
        TrackAsync(connection, "Checking services for updates", item => CheckServicesAsync(connection, client, services, item, cancellationToken),
            s => Outcome(s, "service", "services"));

    private async Task<UpdateCheckSummary> CheckServicesAsync(EngineConnection connection, DockerClient client, IReadOnlyList<SwarmService> services,
        WorkItem? item, CancellationToken cancellationToken)
    {
        var images = services.Select(s => s.Spec?.TaskTemplate?.ContainerSpec?.Image).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var image in images.Where(i => ServiceSubject(i).Length == 0))
        {
            _results[(connection.Id, "", Key(image))] = new UpdateUnknown(
                "The service isn't pinned to a digest (it was deployed with --resolve-image never), so which image its tasks run can't be told.", time.GetUtcNow());
        }
        var work = images.Where(i => ServiceSubject(i).Length > 0).Select(image => new Work(ServiceSubject(image), null, image)).ToList();
        await RunAsync(connection, client, work, item, cancellationToken);
        return UpdateCheckSummary.Of(services.Select(s => ForService(connection.Id, s)));
    }

    /// <summary>
    /// Everything on the endpoint: what containers run and, on a swarm manager, what services are
    /// pinned to. For the scheduled check.
    /// </summary>
    /// <param name="by">Who asked, for the list of background work: <c>schedule</c>.</param>
    public Task CheckEndpointAsync(EngineConnection connection, DockerClient client, string? by = null, CancellationToken cancellationToken = default) =>
        TrackAsync(connection, $"Checking {connection.Name} for updates", async item =>
        {
            var summary = await CheckInUseAsync(connection, client, item, cancellationToken);
            if (connection.Engine?.Has(EngineCapabilities.SwarmManager) == true)
                await CheckServicesAsync(connection, client, await client.ListServicesAsync(cancellationToken: cancellationToken), item, cancellationToken);
            return summary;
        }, _ => Available(connection.Id) is { Count: > 0 } available
            ? $"{available.Count} {(available.Count == 1 ? "image has an update" : "images have updates")}."
            : "No updates found.", by);

    /// <summary>Runs a check as a piece of background work, ending it with the outcome or the failure.</summary>
    private async Task<T> TrackAsync<T>(EngineConnection connection, string title, Func<WorkItem?, Task<T>> run, Func<T, string> outcome, string? by = null)
    {
        using var item = work?.Start(WorkKind.UpdateCheck, title, connection.Id, connection.Name, by: by);
        try
        {
            var result = await run(item);
            item?.Succeed(outcome(result));
            return result;
        }
        catch (OperationCanceledException)
        {
            item?.Canceled();
            throw;
        }
        catch (Exception ex)
        {
            item?.Fail(EngineErrors.ForCall(ex, connection.Address));
            throw;
        }
    }

    /// <summary>"Checked 3 tags: 1 with an update, 2 up to date."</summary>
    private static string Outcome(UpdateCheckSummary summary, string one, string many)
    {
        if (summary.Checked == 0)
            return $"No {many} to check.";
        var parts = new List<string>();
        if (summary.Updates > 0)
            parts.Add($"{summary.Updates} with an update");
        if (summary.UpToDate > 0)
            parts.Add($"{summary.UpToDate} up to date");
        if (summary.Unknown > 0)
            parts.Add($"{summary.Unknown} couldn't be checked");
        return $"Checked {summary.Checked} {(summary.Checked == 1 ? one : many)}: {string.Join(", ", parts)}.";
    }

    /// <summary>Checks one reference of an image and remembers the result.</summary>
    /// <param name="tag">A tag, or a tag pinned to a digest (<c>nginx:1.27@sha256:…</c>), which is compared with that digest.</param>
    public async Task<UpdateStatus> CheckAsync(EngineConnection connection, DockerClient client, string imageId, IReadOnlyList<string>? repoDigests, string tag,
        CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        UpdateStatus status;
        if (ImageReference.Parse(tag) is not { } parsed)
        {
            status = new UpdateUnknown("It isn't a registry reference.", now);
        }
        else if (parsed.Tag is null && parsed.Digest is not null)
        {
            status = new UpdateUnknown("It's pinned to a digest without a tag, so there's nothing newer to look for.", now);
        }
        else if (LocalDigests(repoDigests, tag).Count == 0)
        {
            status = new UpdateUnknown("It has no registry digest: it was built locally or loaded from a file.", now);
        }
        else if (!CanCheck(connection))
        {
            status = new UpdateUnknown(Unsupported(connection), now);
        }
        else
        {
            try
            {
                var (name, tagName) = ImageReference.Split(ServiceSpec.ImageWithoutDigest(tag)!);
                var auth = await registries.AuthHeaderForAsync(tag, cancellationToken);
                var remote = await client.InspectDistributionAsync($"{name}:{tagName}", auth, cancellationToken);
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
        _results[(connection.Id, imageId, Key(tag))] = status;
        return status;
    }

    /// <summary>
    /// Whether the endpoint's engine can say what a registry serves without pulling
    /// (<see cref="EngineCapabilities.RegistryDigests"/>). An endpoint not yet detected is tried.
    /// </summary>
    public static bool CanCheck(EngineConnection connection) => connection.Engine?.Has(EngineCapabilities.RegistryDigests) != false;

    /// <summary>Why an endpoint can't check, for people.</summary>
    public static string Unsupported(EngineConnection connection) =>
        $"{connection.Engine?.DisplayName ?? "This engine"} can't ask a registry for an image's current digest without pulling it. Podman 6.2 and later can.";

    /// <summary>
    /// Up to date when the registry's digest is one the image is known by for that repository.
    /// Docker records the index digest it pulled; Podman records the index and the platform
    /// manifest, so either may match. A reference pinned to a digest is known by that digest.
    /// </summary>
    public static UpdateStatus Compare(IReadOnlyList<string>? repoDigests, string tag, string remoteDigest, DateTimeOffset now)
    {
        var local = LocalDigests(repoDigests, tag);
        if (local.Count == 0)
            return new UpdateUnknown("It has no registry digest: it was built locally or loaded from a file.", now);
        return local.Contains(remoteDigest) ? new UpToDate(now) : new UpdateAvailable(remoteDigest, now);
    }

    /// <summary>
    /// The digests in <paramref name="repoDigests"/> (<c>repo@sha256:…</c>) that belong to the
    /// tag's repository, and the digest the tag itself is pinned to, if any.
    /// </summary>
    internal static HashSet<string> LocalDigests(IReadOnlyList<string>? repoDigests, string tag)
    {
        var parsed = ImageReference.Parse(tag);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        if (parsed is null)
            return digests;
        if (parsed.Digest is { } pinned)
            digests.Add(pinned);
        foreach (var entry in repoDigests ?? [])
        {
            if (ImageReference.Parse(entry) is { Digest: { } digest } other && other.Repository == parsed.Repository)
                digests.Add(digest);
        }
        return digests;
    }

    /// <summary>
    /// One spelling per reference: <c>nginx</c>, <c>nginx:latest</c> and Podman's
    /// <c>docker.io/library/nginx:latest</c> are the same key. A pinned digest stays part of it.
    /// </summary>
    internal static string Key(string reference) => ImageReference.Parse(reference) is { } parsed
        ? $"{parsed.Repository}{(parsed.EffectiveTag is { } tag ? ":" + tag : "")}{(parsed.Digest is { } digest ? "@" + digest : "")}"
        : reference;

    private static string ServiceSubject(string specImage) => ImageReference.Parse(specImage)?.Digest ?? "";

    private sealed record Work(string Subject, IReadOnlyList<string>? RepoDigests, string Reference);

    /// <returns>The result of every reference checked.</returns>
    private async Task<IReadOnlyCollection<UpdateStatus>> CheckAsync(EngineConnection connection, DockerClient client, IReadOnlyList<ContainerSummary> containers, bool allTags,
        WorkItem? item, CancellationToken cancellationToken)
    {
        var images = (await client.ListImagesAsync(cancellationToken)).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var work = new Dictionary<(string, string), Work>();
        void Add(string imageId, string reference) =>
            work.TryAdd((imageId, Key(reference)), new Work(imageId, images.GetValueOrDefault(imageId)?.RepoDigests, reference));

        foreach (var container in containers)
        {
            if (container.ImageId is not { } imageId)
                continue;
            var reference = await ReferenceOfAsync(client, container, cancellationToken);
            var tags = images.GetValueOrDefault(imageId)?.Tags ?? [];
            if (reference is not null)
                Add(imageId, reference);
            if (allTags || reference is null)
            {
                foreach (var tag in tags)
                    Add(imageId, tag);
            }
        }
        return await RunAsync(connection, client, [.. work.Values], item, cancellationToken);
    }

    private async Task<IReadOnlyCollection<UpdateStatus>> RunAsync(EngineConnection connection, DockerClient client, IReadOnlyList<Work> work, WorkItem? progress,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<UpdateStatus>();
        var done = 0;
        progress?.Report(0, work.Count);
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                results.Add(await CheckAsync(connection, client, item.Subject, item.RepoDigests, item.Reference, ct));
                progress?.Report(Interlocked.Increment(ref done), work.Count, WorkUnit.Items, item.Reference);
            });
        return results;
    }

    /// <summary>
    /// The reference a container was created from. Docker lists it by image ID once its tag points
    /// at another image (after a pull), so only then is it inspected for the name in its config.
    /// </summary>
    private static async Task<string?> ReferenceOfAsync(DockerClient client, ContainerSummary container, CancellationToken cancellationToken)
    {
        if (ImageReference.Parse(container.Image) is not null)
            return container.Image;
        try
        {
            var configured = (await client.InspectContainerAsync(container.Id, cancellationToken)).Container.Config?.Image;
            return ImageReference.Parse(configured) is not null ? configured : null;
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return null; // Removed meanwhile.
        }
    }
}
