using Sockless.Port.Engines;

namespace Sockless.Port.Stacks;

/// <summary>
/// Known gaps and common mistakes, recognised in Compose's output after a failed run, with what to
/// do about them: the engine's own (<see cref="EngineHints"/>, several specific to Podman and
/// rootless engines) and Compose's. Matched by the message, so they show only when they actually come up.
/// </summary>
public static class ComposeHints
{
    private const string PullDenied =
        "The registry refused the pull. Check the image name, and add credentials for the registry under Registries if it's private.";

    private const string MissingBindSource =
        "A bind mount's host path doesn't exist on the engine's host. Relative paths (./data) resolve inside Sockless Port's data folder, which the engine can't see: use an absolute host path or a named volume.";

    private static readonly IReadOnlyList<HintRule> Rules =
    [
        .. EngineHints.Rules,
        new(["bind source path does not exist"], MissingBindSource),
        new(["no such file or directory", "mount"], MissingBindSource),
        new(["buildx"],
            "Building images needs Docker Buildx, which the Sockless Port image doesn't include. Build and push the image elsewhere, then use image: here."),
        new(["pull access denied"], PullDenied),
        // Podman's wording of the same refusal.
        new(["requested access to the resource is denied"], PullDenied),
        new(["unauthorized"],
            "The registry refused the credentials. Check them under Registries."),
        new(["manifest unknown"],
            "The registry has no image with that tag. Check the tag."),
        new(["toomanyrequests"],
            "The registry's pull rate limit was reached. Add Docker Hub credentials under Registries, or wait and try again."),
    ];

    /// <summary>Hints for the problems recognised in <paramref name="output"/>, each once, in rule order.</summary>
    public static IReadOnlyList<string> FromOutput(IEnumerable<string> output) => EngineHints.Match(Rules, string.Join('\n', output));
}
