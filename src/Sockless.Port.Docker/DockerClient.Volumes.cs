using System.Net.Http.Json;
using System.Text.Json;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker;

public sealed partial class DockerClient
{
    /// <summary>API version from which a plain volume prune keeps named volumes (Docker 23).</summary>
    private static readonly Version AnonymousVolumePruneVersion = new(1, 42);

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync("/volumes", Json.VolumeListResponse, cancellationToken)).Volumes ?? [];

    /// <summary>Inspects a volume. Returns the typed view and the engine's JSON, verbatim.</summary>
    public async Task<(VolumeSummary Volume, string Json)> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, $"/volumes/{Escape(name)}", content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var volume = JsonSerializer.Deserialize(json, Json.VolumeSummary)
                     ?? throw new InvalidDataException($"The engine returned an empty inspect for volume {name}.");
        return (volume, json);
    }

    public Task<VolumeSummary> CreateVolumeAsync(VolumeCreateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/volumes/create", JsonContent.Create(request, Json.VolumeCreateRequest), Json.VolumeSummary, cancellationToken);

    /// <summary>Removes a volume. The engine refuses (409) while a container, running or not, uses it.</summary>
    /// <param name="force">Also remove it when the driver can't, e.g. its storage is gone.</param>
    public Task RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Path($"/volumes/{Escape(name)}", Query(("force", force ? "true" : null))), content: null, cancellationToken);

    /// <summary>
    /// Whether <see cref="PruneVolumesAsync"/> can keep named volumes. Below API 1.42 a prune
    /// removes every unused volume, named or not.
    /// </summary>
    public bool CanPruneAnonymousVolumesOnly => ApiVersion >= AnonymousVolumePruneVersion;

    /// <summary>
    /// Removes volumes no container uses (stopped containers count as using them).
    /// </summary>
    /// <param name="includeNamed">Also named volumes, not only anonymous ones. Must be true when <see cref="CanPruneAnonymousVolumesOnly"/> is false.</param>
    /// <param name="label">Only volumes with this label: <c>key</c> or <c>key=value</c>.</param>
    public Task<VolumePruneResponse> PruneVolumesAsync(bool includeNamed, string? label = null, CancellationToken cancellationToken = default)
    {
        if (!includeNamed && !CanPruneAnonymousVolumesOnly)
            throw new NotSupportedException($"The engine's API {ApiVersion} prunes named volumes too, so it can't remove only anonymous ones.");
        var filters = new Dictionary<string, string[]>();
        // The "all" filter is unknown below 1.42, where a plain prune already removes everything unused.
        if (includeNamed && CanPruneAnonymousVolumesOnly)
            filters["all"] = ["true"];
        if (label is not null)
            filters["label"] = [label];
        return SendAsync(HttpMethod.Post, Path("/volumes/prune", Query(("filters", Filters(filters)))), content: null, Json.VolumePruneResponse,
            cancellationToken, timeout: _requestTimeout * 4);
    }

    /// <summary>
    /// Volume sizes from <c>/system/df</c>. Slow: the engine walks every volume. Docker computes
    /// only volumes; Podman ignores the type and computes everything.
    /// </summary>
    public async Task<IReadOnlyList<DiskUsageVolume>> GetVolumeUsageAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync(Path("/system/df", Query(("type", "volume"))), Json.DiskUsage, cancellationToken, timeout: _requestTimeout * 4)).Volumes ?? [];
}
