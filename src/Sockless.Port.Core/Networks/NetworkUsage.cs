using Sockless.Port.Docker.Models;

namespace Sockless.Port.Networks;

public static class NetworkUsage
{
    /// <summary>
    /// Network name → the containers attached to it, running or not, from one container list.
    /// A stopped container stays attached, which is what blocks removing the network.
    /// </summary>
    public static ILookup<string, ContainerSummary> ByNetwork(IEnumerable<ContainerSummary> containers) =>
        containers
            .SelectMany(c => (c.NetworkSettings?.Networks?.Keys ?? []).Select(network => (Network: network, Container: c)))
            .OrderBy(x => x.Container.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToLookup(x => x.Network, x => x.Container, StringComparer.Ordinal);
}
