using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Pods;

/// <summary>The pod a container belongs to, and whether it's the pod's infra container (which holds the shared namespaces).</summary>
public sealed record PodMember(PodSummary Pod, bool IsInfra);

/// <summary>
/// Which pod each container is in. Docker's container list doesn't say, even on Podman, so it comes
/// from libpod's pod list, which names each pod's containers.
/// </summary>
public sealed class PodMembership
{
    private readonly Dictionary<string, PodMember> _byContainer;

    private PodMembership(IReadOnlyList<PodSummary> pods)
    {
        Pods = pods;
        _byContainer = new Dictionary<string, PodMember>(StringComparer.Ordinal);
        foreach (var pod in pods)
        {
            foreach (var container in pod.Containers ?? [])
                _byContainer[container.Id] = new PodMember(pod, pod.IsInfra(container.Id));
        }
    }

    public static PodMembership Empty { get; } = new([]);

    public IReadOnlyList<PodSummary> Pods { get; }

    public static PodMembership From(IReadOnlyList<PodSummary> pods) => new(pods);

    public PodMember? Of(string containerId) => _byContainer.GetValueOrDefault(containerId);

    /// <summary>
    /// Reads the pods on engines that have them, and nothing on others. A failure leaves the
    /// containers without pods rather than failing the page that wanted them.
    /// </summary>
    public static async Task<PodMembership> LoadAsync(DockerClient client, EngineDescriptor? engine, CancellationToken cancellationToken)
    {
        if (engine?.Has(EngineCapabilities.Pods) != true)
            return Empty;
        try
        {
            return From(await client.ListPodsAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Empty;
        }
    }

    /// <summary>Whether the pod holds the container Sockless Port runs in, so stopping or removing it would end the request.</summary>
    public static bool IsSelf(PodSummary pod) => (pod.Containers ?? []).Any(c => SelfContainer.Is(c.Id));

    /// <inheritdoc cref="IsSelf(PodSummary)"/>
    public static bool IsSelf(PodInspect pod) => (pod.Containers ?? []).Any(c => SelfContainer.Is(c.Id));
}
