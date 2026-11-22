using System.Net.Http.Json;
using System.Text.Json;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker;

/// <summary>
/// Podman pods, through libpod's own API on the same socket (Docker's API has no pods). Only for
/// engines with the <c>Pods</c> capability; Docker answers 404.
/// </summary>
/// <remarks>Paths start with <see cref="LibpodPrefix"/>, never through <c>Path()</c>, which adds the compatible API's version.</remarks>
public sealed partial class DockerClient
{
    /// <summary>
    /// Libpod's API version. Podman serves every version from its <c>MinAPIVersion</c> (4.0.0 since
    /// Podman 4) up to its own, and pods haven't changed shape since 4.0.
    /// </summary>
    public const string LibpodPrefix = "/v4.0.0/libpod";

    public async Task<IReadOnlyList<PodSummary>> ListPodsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync($"{LibpodPrefix}/pods/json", Json.ListPodSummary, cancellationToken);

    /// <summary>Inspects a pod by ID or name. Returns the typed view and the engine's JSON, verbatim.</summary>
    public async Task<(PodInspect Pod, string Json)> InspectPodAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, $"{LibpodPrefix}/pods/{Escape(idOrName)}/json", content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var pod = JsonSerializer.Deserialize(json, Json.PodInspect)
                  ?? throw new InvalidDataException($"The engine returned an empty inspect for pod {idOrName}.");
        return (pod, json);
    }

    /// <summary>Starts every container of the pod, infra first. A pod already running is left alone (304).</summary>
    /// <exception cref="DockerApiException">409 naming the containers that failed, when only some started.</exception>
    public Task StartPodAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{LibpodPrefix}/pods/{Escape(idOrName)}/start", content: null, cancellationToken,
            timeout: _requestTimeout * 2, allowNotModified: true);

    /// <param name="timeout">Seconds each container gets to stop before it's killed; null for the engine's default (10).</param>
    public Task StopPodAsync(string idOrName, int? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{LibpodPrefix}/pods/{Escape(idOrName)}/stop" + Query(("t", Number(timeout))), content: null, cancellationToken,
            timeout: StopTimeout(timeout), allowNotModified: true);

    public Task RestartPodAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{LibpodPrefix}/pods/{Escape(idOrName)}/restart", content: null, cancellationToken, timeout: StopTimeout(null));

    /// <summary>
    /// Removes the pod and its containers. Always forced: without force, libpod refuses a running pod
    /// only after removing its stopped containers (the infra one too), leaving it half removed.
    /// </summary>
    public Task<PodRemoveReport> RemovePodAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"{LibpodPrefix}/pods/{Escape(idOrName)}?force=true", content: null, Json.PodRemoveReport,
            cancellationToken, timeout: StopTimeout(null));

    /// <summary>Creates a pod with an infra container. Returns its ID.</summary>
    public async Task<string> CreatePodAsync(PodCreateRequest request, CancellationToken cancellationToken = default) =>
        (await SendAsync(HttpMethod.Post, $"{LibpodPrefix}/pods/create", JsonContent.Create(request, Json.PodCreateRequest), Json.IdResponse, cancellationToken)).Id;

    /// <summary>Creates (doesn't start) a container in a pod. Returns its ID.</summary>
    public async Task<string> CreatePodContainerAsync(PodContainerCreateRequest request, CancellationToken cancellationToken = default) =>
        (await SendAsync(HttpMethod.Post, $"{LibpodPrefix}/containers/create", JsonContent.Create(request, Json.PodContainerCreateRequest), Json.IdResponse,
            cancellationToken)).Id;

    /// <summary>Stopping waits for every container's grace period, so allow for it on top of the request timeout.</summary>
    private TimeSpan StopTimeout(int? seconds) => _requestTimeout + TimeSpan.FromSeconds((seconds ?? 10) + 5);
}
