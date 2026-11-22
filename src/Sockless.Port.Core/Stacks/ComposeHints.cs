namespace Sockless.Port.Stacks;

/// <summary>
/// Known gaps and common mistakes, recognised in Compose's output after a failed run, with what to
/// do about them. Several are specific to Podman's Docker-compatible API; they're matched by the
/// engine's message, so they show only when they actually come up.
/// </summary>
public static class ComposeHints
{
    private const string PullDenied =
        "The registry refused the pull. Check the image name, and add credentials for the registry under Registries if it's private.";

    private static readonly IReadOnlyList<(string[] AllOf, string Hint)> Rules =
    [
        (["short-name", "did not resolve"],
            "Podman can't tell which registry a short image name such as nginx comes from. Use the full name, e.g. docker.io/library/nginx, or add unqualified-search-registries to the engine's registries.conf."),
        (["privileged port"],
            "Rootless engines can't publish ports below 1024. Publish a higher port (8080:80), or lower net.ipv4.ip_unprivileged_port_start on the host."),
        (["rootlessport"],
            "Rootless Podman forwards published ports through rootlessport, which failed. Check that the host port is free and above 1024."),
        (["port is already allocated"],
            "Another container already publishes that host port. Change the host side of ports:, or stop the other container."),
        (["address already in use"],
            "Something on the host already listens on that port. Change the host side of ports:."),
        (["cgroup"],
            "The engine can't apply resource limits here. Rootless Podman needs cgroups v2 with the cpu, memory and pids controllers delegated to the user; nested engines often have none."),
        (["bind source path does not exist"],
            "A bind mount's host path doesn't exist on the engine's host. Relative paths (./data) resolve inside Sockless Port's data folder, which the engine can't see: use an absolute host path or a named volume."),
        (["no such file or directory", "mount"],
            "A bind mount's host path doesn't exist on the engine's host. Relative paths (./data) resolve inside Sockless Port's data folder, which the engine can't see: use an absolute host path or a named volume."),
        (["lsetxattr"],
            "SELinux refused to relabel a bind mount. Add :z (shared) or :Z (private) to the volume, e.g. /srv/app:/app:z, or mount a path that's already labelled."),
        (["buildx"],
            "Building images needs Docker Buildx, which the Sockless Port image doesn't include. Build and push the image elsewhere, then use image: here."),
        (["pull access denied"], PullDenied),
        // Podman's wording of the same refusal.
        (["requested access to the resource is denied"], PullDenied),
        (["unauthorized"],
            "The registry refused the credentials. Check them under Registries."),
        (["manifest unknown"],
            "The registry has no image with that tag. Check the tag."),
        (["toomanyrequests"],
            "The registry's pull rate limit was reached. Add Docker Hub credentials under Registries, or wait and try again."),
    ];

    /// <summary>Hints for the problems recognised in <paramref name="output"/>, each once, in rule order.</summary>
    public static IReadOnlyList<string> FromOutput(IEnumerable<string> output)
    {
        var text = string.Join('\n', output);
        var hints = new List<string>();
        foreach (var (allOf, hint) in Rules)
        {
            if (allOf.All(part => text.Contains(part, StringComparison.OrdinalIgnoreCase)) && !hints.Contains(hint))
                hints.Add(hint);
        }
        return hints;
    }
}
