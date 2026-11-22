using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Pods;

namespace Sockless.Port.Web.Infrastructure;

public static class PodStatus
{
    /// <summary>A pod's state as a word badge, coloured only when it means something.</summary>
    public static StatusBadge State(string? state) => state switch
    {
        PodStates.Running => new("Running", "success"),
        PodStates.Degraded => new("Degraded", "warning"),
        PodStates.Paused => new("Paused", "warning"),
        PodStates.Error => new("Error", "danger"),
        PodStates.Created => new("Created", ""),
        PodStates.Exited or PodStates.Stopped => new(state, ""),
        { Length: > 0 } other => new(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.ToLowerInvariant()), ""),
        _ => new("Unknown", ""),
    };

    /// <summary>What a state means, for the badge's title.</summary>
    public static string? Explain(string? state) => state switch
    {
        PodStates.Degraded => "Some of the pod's containers run and others don't",
        PodStates.Created => "Created but never started",
        _ => null,
    };

    /// <summary>Published ports of a pod, from its infra container: "8080→80/tcp".</summary>
    public static IReadOnlyList<string> PublishedPorts(PodInfraConfig? infra) =>
        infra?.PortBindings is not { } bindings
            ? []
            : [.. bindings
                .SelectMany(b => (b.Value ?? []).Select(binding => (Container: b.Key, binding.HostIp, binding.HostPort)))
                .Where(b => b.HostPort is { Length: > 0 })
                .Select(b => (b.Container, b.HostPort, Ip: b.HostIp is { Length: > 0 } and not ("0.0.0.0" or "::") ? b.HostIp : null))
                .Distinct()
                .OrderBy(b => int.TryParse(b.HostPort, CultureInfo.InvariantCulture, out var port) ? port : int.MaxValue)
                .Select(b => $"{(b.Ip is null ? "" : b.Ip + ":")}{b.HostPort}→{(b.Container.Contains('/', StringComparison.Ordinal) ? b.Container : b.Container + "/tcp")}")];
}

/// <summary>The pod's containers, with their list entry (state, image) when the engine still lists them.</summary>
public sealed record PodContainerRow(string Id, string Name, bool IsInfra, ContainerSummary? Summary)
{
    public bool Running => Summary?.State is "running";
}

/// <summary>Shared by the pod pages: whether the endpoint has pods at all.</summary>
public abstract class PodsPageModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public bool HasPods => Connection.Engine?.Has(EngineCapabilities.Pods) == true;

    /// <summary>The client while online on an engine with pods; otherwise null, and <c>_PodsCallout</c> explains.</summary>
    public DockerClient? Pods => HasPods ? Client : null;

    public string? Error { get; protected set; }
}

/// <summary>
/// Base for a pod's pages (<c>/endpoints/{endpointId}/pods/{podId}/…</c>, by ID or name): inspects
/// the pod and lists its containers, once per request.
/// </summary>
public abstract partial class PodPageModel(EngineConnections connections) : PodsPageModel(connections)
{
    public const string RouteKey = "podId";

    /// <summary>The ID or name from the route.</summary>
    public string PodKey { get; private set; } = "";

    public PodInspect? Pod { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>The pod's containers by name, its infra container last.</summary>
    public IReadOnlyList<PodContainerRow> Members { get; private set; } = [];

    /// <summary>The pod holds the container Sockless Port runs in: it can be started, but not stopped, restarted or removed from here.</summary>
    public bool IsSelf { get; private set; }

    /// <summary>For links and form routes: the ID once inspected, otherwise what the route had.</summary>
    public string PodId => Pod?.Id ?? PodKey;

    public string DisplayName => Pod?.Name ?? PodKey;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        PodKey = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!PodKeyPattern().IsMatch(PodKey))
            return NotFound();

        if (Pods is not { } client)
            return null;
        try
        {
            (Pod, InspectJson) = await client.InspectPodAsync(PodKey, cancellationToken);
            IsSelf = PodMembership.IsSelf(Pod);
            var ids = (Pod.Containers ?? []).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var listed = (await client.ListContainersAsync(all: true, cancellationToken))
                .Where(c => ids.Contains(c.Id))
                .ToDictionary(c => c.Id, StringComparer.Ordinal);
            Members = [.. (Pod.Containers ?? [])
                .Select(c => new PodContainerRow(c.Id, c.Name is { Length: > 0 } name ? name : c.Id[..Math.Min(12, c.Id.Length)], Pod.IsInfra(c.Id), listed.GetValueOrDefault(c.Id)))
                .OrderBy(c => c.IsInfra)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return NotFound();
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
        return null;
    }

    /// <summary>Containers other than the infra one.</summary>
    public int Workloads => Members.Count(m => !m.IsInfra);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex PodKeyPattern();
}
