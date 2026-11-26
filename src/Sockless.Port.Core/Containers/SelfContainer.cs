using Sockless.Port.Docker;

namespace Sockless.Port.Containers;

/// <summary>
/// The container Sockless Port itself runs in, if any, so it never stops, removes or recreates
/// itself (a <c>docker compose down</c> of its own project would end the request that asked for it).
/// </summary>
public static class SelfContainer
{
    /// <summary>Whether <paramref name="containerId"/> (full or short) is the container we run in.</summary>
    public static bool Is(string containerId) => OwnContainer.Is(containerId);

    internal static bool Matches(IReadOnlyList<string> ids, string containerId) => OwnContainer.Matches(ids, containerId);

    /// <inheritdoc cref="OwnContainer.Parse"/>
    internal static IReadOnlyList<string> Parse(string? mountInfo, string? cgroup, string? hostname) => OwnContainer.Parse(mountInfo, cgroup, hostname);
}
