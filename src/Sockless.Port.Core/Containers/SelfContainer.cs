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
    /// Container IDs from our own mounts: Docker and Podman bind <c>/etc/hostname</c> (and <c>/etc/hosts</c>,
    /// <c>/etc/resolv.conf</c>) from a folder named after the container (<c>…/containers/{id}/hostname</c>,
    /// <c>…/overlay-containers/{id}/userdata/hostname</c>), and cgroup paths name it too. Falls back to
    /// the hostname, which Docker sets to the short ID.
    /// </summary>
    /// <remarks>
    /// Only those mount points count: on a host with a rootful engine, mountinfo lists every running
    /// container's own mounts (<c>…/overlay-containers/{id}/userdata/shm</c>, <c>…/containers/{id}/mounts/shm</c>),
    /// which would make every container look like ours.
    /// </remarks>
    internal static IReadOnlyList<string> Parse(string? mountInfo, string? cgroup, string? hostname)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (mountInfo ?? "").Split('\n'))
        {
            // mountinfo: id parent major:minor root mountpoint options …
            var fields = line.Split(' ');
            if (fields.Length > 4 && fields[4] is "/etc/hostname" or "/etc/hosts" or "/etc/resolv.conf")
                AddIds(ids, fields[3]);
        }
        AddIds(ids, cgroup);
        if (ids.Count == 0 && hostname is { Length: 12 } && hostname.All(char.IsAsciiHexDigitLower))
            ids.Add(hostname);
        return [.. ids];
    }

    private static void AddIds(HashSet<string> ids, string? text)
    {
        if (text is null)
            return;
        foreach (Match match in IdPattern().Matches(text))
            ids.Add(match.Groups[1].Value);
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
