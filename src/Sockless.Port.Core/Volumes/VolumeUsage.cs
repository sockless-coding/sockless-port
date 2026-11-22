using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Volumes;

/// <summary>A container that mounts a volume.</summary>
/// <param name="Destination">Where the volume appears inside the container.</param>
public sealed record VolumeUse(ContainerSummary Container, string? Destination, bool ReadWrite);

public static class VolumeUsage
{
    /// <summary>
    /// Volume name → the containers mounting it, running or not, from one container list (its
    /// <c>Mounts</c>). Sockless Port's own helpers don't count.
    /// </summary>
    public static ILookup<string, VolumeUse> ByVolume(IEnumerable<ContainerSummary> containers) =>
        containers
            .Where(c => !ContainerLabels.IsHelper(c.Labels))
            .SelectMany(c => (c.Mounts ?? []).Where(m => m.Type == "volume" && !string.IsNullOrEmpty(m.Name))
                .Select(m => (Volume: m.Name!, Use: new VolumeUse(c, m.Destination, m.ReadWrite))))
            .OrderBy(x => x.Use.Container.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToLookup(x => x.Volume, x => x.Use, StringComparer.Ordinal);
}
