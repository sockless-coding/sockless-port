namespace Sockless.Port.Engines;

/// <summary>Recognises a known problem by words in the engine's message; <see cref="NoneOf"/> leaves it to a more specific rule.</summary>
public sealed record HintRule(string[] AllOf, string Hint, string[]? NoneOf = null)
{
    public bool Matches(string text) =>
        AllOf.All(part => text.Contains(part, StringComparison.OrdinalIgnoreCase))
        && (NoneOf ?? []).All(part => !text.Contains(part, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Known gaps of rootless and Podman engines, recognised in an engine's error message, with what to
/// do about them. Matched by the message, so they show only when the problem actually comes up.
/// Compose runs add their own (<see cref="Stacks.ComposeHints"/>).
/// </summary>
public static class EngineHints
{
    public static IReadOnlyList<HintRule> Rules { get; } =
    [
        new(["short-name", "did not resolve"],
            "Podman can't tell which registry a short image name such as nginx comes from. Use the full name, e.g. docker.io/library/nginx, or add unqualified-search-registries to the engine's registries.conf."),
        // Rootless Podman: "rootlessport cannot expose privileged port 80"; rootless Docker: "RootlessKit PortManager.AddPort(): cannot expose privileged port 80".
        new(["privileged port"],
            "Rootless engines can't publish ports below 1024. Publish a higher port (8080:80), or lower net.ipv4.ip_unprivileged_port_start on the host."),
        new(["rootlessport"],
            "Rootless Podman forwards published ports through rootlessport, which failed. Check that the host port is free and above 1024."),
        new(["port is already allocated"],
            "Another container already publishes that host port. Change the host side of the port, or stop the other container."),
        new(["address already in use"],
            "Something on the host already listens on that port. Change the host side of the port."),
        // Stats, top and pause of a container started without a cgroup (nested engines, rootless on cgroups v1).
        new(["does not have a cgroup"],
            "The container has no cgroup of its own, so the engine can't measure, limit or pause it. Rootless engines need cgroups v2 with the cpu, memory, io and pids controllers delegated to the user; nested engines often have none."),
        new(["cgroup"],
            "The engine can't use cgroups here, which resource limits, stats and pausing need. Rootless engines need cgroups v2 with the cpu, memory and pids controllers delegated to the user; nested engines often have none.",
            NoneOf: ["does not have a cgroup"]),
        new(["lsetxattr"],
            "SELinux refused to relabel a bind mount. Add :z (shared) or :Z (private) to the volume, e.g. /srv/app:/app:z, or mount a path that's already labelled."),
    ];

    /// <summary>Hints for the problems recognised in <paramref name="text"/>, in rule order.</summary>
    public static IReadOnlyList<string> For(string text) => Match(Rules, text);

    /// <summary>Each matching rule's hint once, in rule order.</summary>
    public static IReadOnlyList<string> Match(IEnumerable<HintRule> rules, string text)
    {
        var hints = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Matches(text) && !hints.Contains(rule.Hint))
                hints.Add(rule.Hint);
        }
        return hints;
    }
}
