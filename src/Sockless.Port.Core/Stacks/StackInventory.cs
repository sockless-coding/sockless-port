using System.Globalization;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Stacks;

/// <summary>Where a stack's containers are, as a word for its badge.</summary>
public enum StackState
{
    /// <summary>No containers: never deployed, or taken down.</summary>
    Down,

    Running,

    /// <summary>Some containers run, others don't.</summary>
    Partial,

    /// <summary>Containers exist but none runs.</summary>
    Stopped,
}

/// <summary>A Compose service on the engine and its containers (replicas).</summary>
public sealed record StackService(string Name, IReadOnlyList<ContainerSummary> Containers)
{
    public int Running => Containers.Count(StackInventory.IsRunning);

    public string? Image => Containers.FirstOrDefault()?.Image;
}

/// <summary>A Compose project as the engine sees it: its containers, found by label.</summary>
public sealed record ComposeProject(string Name, IReadOnlyList<ContainerSummary> Containers)
{
    public static ComposeProject Empty(string name) => new(name, []);

    public IReadOnlyList<StackService> Services { get; } = [.. Containers
        .GroupBy(c => c.Labels?.GetValueOrDefault(ContainerLabels.ComposeService) ?? c.DisplayName, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new StackService(g.Key, [.. g.OrderBy(StackInventory.Number).ThenBy(c => c.DisplayName, StringComparer.Ordinal)]))];

    public int Running => Containers.Count(StackInventory.IsRunning);

    public StackState State => Containers.Count == 0 ? StackState.Down
        : Running == Containers.Count ? StackState.Running
        : Running == 0 ? StackState.Stopped
        : StackState.Partial;

    /// <summary>The folder it was deployed from, on the machine that ran Compose.</summary>
    public string? WorkingDirectory => Label(StackInventory.WorkingDirLabel);

    /// <summary>The compose files it was deployed from (comma-separated in the label), on that machine.</summary>
    public IReadOnlyList<string> ConfigFiles => Label(StackInventory.ConfigFilesLabel) is { Length: > 0 } files
        ? files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    /// <summary>Sockless Port runs in one of its containers, so it must not be stopped or redeployed from here.</summary>
    public bool IsSelf => Containers.Any(c => SelfContainer.Is(c.Id));

    private string? Label(string key) => Containers.Select(c => c.Labels?.GetValueOrDefault(key)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

/// <summary>Groups an engine's containers into Compose projects.</summary>
public static class StackInventory
{
    internal const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    internal const string ConfigFilesLabel = "com.docker.compose.project.config_files";
    private const string NumberLabel = "com.docker.compose.container-number";
    private const string OneOffLabel = "com.docker.compose.oneoff";

    /// <summary>
    /// Every Compose project with containers, keyed by name. One-off containers (<c>compose run</c>)
    /// and Sockless Port's helpers aren't part of a project's services.
    /// </summary>
    public static IReadOnlyDictionary<string, ComposeProject> Projects(IEnumerable<ContainerSummary> containers) =>
        containers
            .Where(c => c.Labels?.GetValueOrDefault(ContainerLabels.ComposeProject) is { Length: > 0 }
                        && !string.Equals(c.Labels.GetValueOrDefault(OneOffLabel), "True", StringComparison.OrdinalIgnoreCase)
                        && !ContainerLabels.IsHelper(c.Labels))
            .GroupBy(c => c.Labels![ContainerLabels.ComposeProject], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new ComposeProject(g.Key, [.. g]), StringComparer.Ordinal);

    internal static bool IsRunning(ContainerSummary container) => container.State == "running";

    internal static int Number(ContainerSummary container) =>
        int.TryParse(container.Labels?.GetValueOrDefault(NumberLabel), CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;
}
