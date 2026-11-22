using System.Text.RegularExpressions;

namespace Sockless.Port.Containers;

/// <summary>
/// The container Sockless Port itself runs in, if any, so it never stops, removes or recreates
/// itself (a <c>docker compose down</c> of its own project would end the request that asked for it).
/// </summary>
public static partial class SelfContainer
{
    private static readonly Lazy<IReadOnlyList<string>> Ids = new(Find);

    /// <summary>Whether <paramref name="containerId"/> (full or short) is the container we run in.</summary>
    public static bool Is(string containerId) => Matches(Ids.Value, containerId);

    internal static bool Matches(IReadOnlyList<string> ids, string containerId) =>
        containerId.Length >= 12 && ids.Any(id => id.StartsWith(containerId, StringComparison.Ordinal) || containerId.StartsWith(id, StringComparison.Ordinal));

    /// <summary>
    /// Container IDs from our own mounts: Docker and Podman bind <c>/etc/hostname</c> from a folder
    /// named after the container (<c>…/containers/{id}/hostname</c>, <c>…/overlay-containers/{id}/userdata/hostname</c>),
    /// and cgroup paths name it too. Falls back to the hostname, which Docker sets to the short ID.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string? mountInfo, string? cgroup, string? hostname)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in (string?[])[mountInfo, cgroup])
        {
            if (text is null)
                continue;
            foreach (Match match in IdPattern().Matches(text))
                ids.Add(match.Groups[1].Value);
        }
        if (ids.Count == 0 && hostname is { Length: 12 } && hostname.All(char.IsAsciiHexDigitLower))
            ids.Add(hostname);
        return [.. ids];
    }

    private static IReadOnlyList<string> Find()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        return Parse(TryRead("/proc/self/mountinfo"), TryRead("/proc/self/cgroup"), Environment.GetEnvironmentVariable("HOSTNAME"));
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("(?:containers/|docker-|libpod-)([0-9a-f]{64})(?![0-9a-f])")]
    private static partial Regex IdPattern();
}
