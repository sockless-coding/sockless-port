using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Pods;
using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Web.Infrastructure;

/// <param name="EndpointId">The endpoint it runs on.</param>
/// <param name="Pod">The Podman pod it's in, if any.</param>
/// <param name="Update">The last update check's result for what it runs, if it was checked.</param>
public sealed record ContainerRow(int EndpointId, ContainerSummary Summary, StatusBadge State, StatusBadge? Health, string? Stack, string? SwarmService,
    IReadOnlyList<string> Ports, PodMember? Pod = null, UpdateStatus? Update = null, ScanFlag? Scan = null)
{
    public string Name => Summary.DisplayName;
    public bool Running => Summary.State is "running";

    /// <summary>State and health as the one badge the list shows.</summary>
    public StatusBadge Badge => ContainerStatus.Combined(State, Health);

    /// <summary>May the user start, stop and remove it (operator on its endpoint)? Rows without it show no actions.</summary>
    public bool CanWrite { get; init; }
}

/// <summary>
/// The containers of one stack on one endpoint, or of none (<paramref name="Stack"/> null), as the
/// lists group them. <paramref name="Endpoint"/> is set on the list across endpoints, where the
/// group's heading names it.
/// </summary>
public sealed record ContainerGroup(EngineConnection? Endpoint, string? Stack, IReadOnlyList<ContainerRow> Rows)
{
    public int Running => Rows.Count(r => r.Running);

    /// <summary>The group's select box key: endpoint and stack ("3:web", "3:" for no stack).</summary>
    public string Key => $"{Endpoint?.Id}:{Stack}";

    /// <summary>
    /// Stacks by name (the same stack on several endpoints next to each other, in the endpoints'
    /// order), then the containers without a stack, by endpoint.
    /// </summary>
    public static IReadOnlyList<ContainerGroup> Of(IEnumerable<ContainerRow> rows, IReadOnlyList<EngineConnection>? endpoints = null)
    {
        int Order(int endpointId)
        {
            for (var i = 0; i < (endpoints?.Count ?? 0); i++)
            {
                if (endpoints![i].Id == endpointId)
                    return i;
            }
            return int.MaxValue;
        }
        return [.. rows
            .GroupBy(r => (Endpoint: endpoints is null ? 0 : r.EndpointId, r.Stack))
            .OrderBy(g => g.Key.Stack is null)
            .ThenBy(g => g.Key.Stack, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => Order(g.Key.Endpoint))
            .Select(g => new ContainerGroup(endpoints?.FirstOrDefault(c => c.Id == g.Key.Endpoint), g.Key.Stack,
                [.. g.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)]))];
    }
}

/// <summary>What a container list asks for. <see cref="Pod"/> is a pod's ID; <see cref="Label"/> <c>key</c> or <c>key=value</c>.</summary>
public sealed record ContainerFilter(string? State = null, string? Q = null, string? Stack = null, string? Pod = null, string? Label = null)
{
    public bool IsSet => !string.IsNullOrEmpty(State) || !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Stack) || !string.IsNullOrEmpty(Pod) || !string.IsNullOrWhiteSpace(Label);
}

/// <summary>One endpoint's containers as a list shows them, and what its filters offer.</summary>
/// <param name="Total">Every container on the endpoint, before filtering.</param>
/// <param name="Stacks">The stacks its containers are in, for the filter.</param>
/// <param name="Pods">Its pods, for the filter; empty on engines without pods.</param>
public sealed record ContainerList(IReadOnlyList<ContainerRow> Rows, int Total, IReadOnlyList<string> Stacks, IReadOnlyList<PodSummary> Pods);

/// <summary>Reads an endpoint's containers for the lists: the endpoint's own and the one across endpoints.</summary>
public sealed class ContainerListing(ImageUpdates updates, ImageScans scans)
{
    public static readonly IReadOnlyList<(string Value, string Label)> States =
        [("", "All"), ("running", "Running"), ("stopped", "Stopped"), ("paused", "Paused"), ("unhealthy", "Unhealthy"), ("update", "Update available")];

    /// <summary>Reads the containers. Engine failures are the caller's to describe.</summary>
    public async Task<ContainerList> LoadAsync(EngineConnection connection, DockerClient client, ContainerFilter filter, CancellationToken cancellationToken)
    {
        var all = await client.ListContainersAsync(all: true, cancellationToken);
        var pods = await PodMembership.LoadAsync(client, connection.Engine, cancellationToken);
        var shown = all.Where(c => Matches(connection.Id, c, pods.Of(c.Id), filter)).ToList();
        var scanned = await scans.SummariesAsync(shown.Select(c => c.ImageId).OfType<string>(), cancellationToken);
        return new ContainerList(
            [.. shown.Select(c => ToRow(connection.Id, c, pods.Of(c.Id), scanned)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
            all.Count,
            [.. all.Select(c => ContainerLabels.Stack(c.Labels)).OfType<string>().Distinct().Order(StringComparer.OrdinalIgnoreCase)],
            [.. pods.Pods.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private bool Matches(int endpointId, ContainerSummary c, PodMember? pod, ContainerFilter filter)
    {
        var stateMatches = filter.State switch
        {
            "running" => c.State is "running",
            "stopped" => c.State is "exited" or "created" or "dead",
            "paused" => c.State is "paused",
            "unhealthy" => c.IsUnhealthy,
            "update" => updates.ForContainer(endpointId, c) is UpdateAvailable,
            _ => true,
        };
        if (!stateMatches)
            return false;

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var q = filter.Q.Trim();
            if (!c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && c.Image?.Contains(q, StringComparison.OrdinalIgnoreCase) != true
                && !c.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(filter.Stack) && ContainerLabels.Stack(c.Labels) != filter.Stack)
            return false;

        if (!string.IsNullOrEmpty(filter.Pod) && pod?.Pod.Id != filter.Pod)
            return false;

        if (!string.IsNullOrWhiteSpace(filter.Label))
        {
            var label = filter.Label;
            var equals = label.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? label : label[..equals]).Trim();
            if (c.Labels?.TryGetValue(key, out var value) != true)
                return false;
            if (equals >= 0 && value != label[(equals + 1)..].Trim())
                return false;
        }
        return true;
    }

    private ContainerRow ToRow(int endpointId, ContainerSummary c, PodMember? pod, IReadOnlyDictionary<string, ScanSummary> scanned)
    {
        var (state, health) = ContainerStatus.FromSummary(c);
        return new ContainerRow(endpointId, c, state, health, ContainerLabels.Stack(c.Labels), ContainerLabels.SwarmServiceName(c.Labels), ContainerStatus.CompactPorts(c.Ports), pod,
            updates.ForContainer(endpointId, c),
            c.ImageId is { } imageId ? new ScanFlag(scanned.GetValueOrDefault(imageId), scans.StateOf(imageId)) : null);
    }
}

/// <summary>One action on several containers of one endpoint, as the lists' bulk and row buttons run it.</summary>
public static class ContainerBulk
{
    /// <summary>The most containers one post acts on.</summary>
    public const int Max = 500;

    /// <summary>Runs the action on each, one at a time: parallel stops of dependent containers race, and the engine serialises anyway.</summary>
    /// <returns>The names of the containers it was done to, and a message for each it failed on.</returns>
    public static async Task<(List<string> Done, List<string> Failed)> RunAsync(EngineConnection connection, DockerClient client, ContainerAction action,
        IEnumerable<string> ids, bool removeVolumes, CancellationToken cancellationToken)
    {
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var id in ids.Distinct().Take(Max))
        {
            var name = await NameOfAsync(client, id, cancellationToken);
            try
            {
                await ContainerActions.RunAsync(client, action, id, removeVolumes, cancellationToken);
                done.Add(name);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{name}: {EngineFailures.Describe(ex, connection)}");
            }
        }
        return (done, failed);
    }

    /// <summary>"web, db" for up to three, else "5 containers".</summary>
    public static string Describe(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{names.Count} containers";

    private static async Task<string> NameOfAsync(DockerClient client, string id, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.InspectContainerAsync(id, cancellationToken)).Container.DisplayName;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return id.Length > 12 ? id[..12] : id;
        }
    }
}

/// <summary>
/// What the container table shows. On the list across endpoints (<see cref="AcrossEndpoints"/>)
/// every group names its endpoint, and boxes and row buttons carry <c>endpointId:containerId</c>.
/// </summary>
/// <param name="ActionUrl">Where a row's button posts an action ("stop") for the row.</param>
public sealed record ContainerTableView(IReadOnlyList<ContainerGroup> Groups, bool AcrossEndpoints, Func<ContainerRow, string, string> ActionUrl)
{
    /// <summary>Headings are worth showing across endpoints, or when some container is in a stack.</summary>
    public bool ShowGroups => AcrossEndpoints || Groups.Any(g => g.Stack is not null);

    /// <summary>Selection boxes show when the user may act on some row.</summary>
    public bool AnyWrite => Groups.Any(g => g.Rows.Any(r => r.CanWrite));

    public string SelectValue(ContainerRow row) => AcrossEndpoints ? $"{row.EndpointId}:{row.Summary.Id}" : row.Summary.Id;
}
