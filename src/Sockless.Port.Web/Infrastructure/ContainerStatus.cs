using System.Globalization;
using System.Text.RegularExpressions;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>A container's state as a badge: always a word, coloured only when it means something.</summary>
/// <param name="Kind">Badge class: <c>success</c>, <c>warning</c>, <c>danger</c>, or empty for a neutral stat badge.</param>
public sealed record StatusBadge(string Label, string Kind);

public static partial class ContainerStatus
{
    public static StatusBadge State(string? state, int? exitCode) => state switch
    {
        "running" => new("Running", "success"),
        "paused" => new("Paused", "warning"),
        "restarting" => new("Restarting…", "warning"),
        "removing" => new("Removing…", "warning"),
        "created" => new("Created", ""),
        "dead" => new("Dead", "danger"),
        "exited" when exitCode is > 0 and var code => new($"Exited ({code.ToString(CultureInfo.InvariantCulture)})", "danger"),
        "exited" => new("Exited", ""),
        _ => new(state is { Length: > 0 } ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(state) : "Unknown", ""),
    };

    public static StatusBadge? Health(string? health) => health switch
    {
        "healthy" => new("Healthy", "success"),
        "unhealthy" => new("Unhealthy", "danger"),
        "starting" => new("Health: starting…", "warning"),
        _ => null,
    };

    /// <summary>From a list item, whose status text carries the exit code and health: "Exited (1) 3 hours ago", "Up 2 hours (healthy)".</summary>
    public static (StatusBadge State, StatusBadge? Health) FromSummary(ContainerSummary summary)
    {
        var status = summary.Status ?? "";
        int? exitCode = ExitCode().Match(status) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var health = status.Contains("(healthy)", StringComparison.Ordinal) ? "healthy"
            : status.Contains("(unhealthy)", StringComparison.Ordinal) ? "unhealthy"
            : status.Contains("health: starting", StringComparison.Ordinal) ? "starting"
            : null;
        return (State(summary.State, exitCode), Health(health));
    }

    public static (StatusBadge State, StatusBadge? Health) FromInspect(ContainerInspect container)
    {
        var state = container.State;
        var name = state switch
        {
            null => null,
            { Paused: true } => "paused",
            { Restarting: true } => "restarting",
            _ => state.Status,
        };
        return (State(name, state?.ExitCode), Health(state?.Health?.Status));
    }

    /// <summary>Published ports as "8080→80/tcp", merging the IPv4 and IPv6 bindings of the same port.</summary>
    public static IReadOnlyList<string> PublishedPorts(IReadOnlyList<ContainerPort>? ports) =>
        ports is null
            ? []
            : [.. ports.Where(p => p.PublicPort > 0)
                .GroupBy(p => (p.PublicPort, p.PrivatePort, Type: p.Type ?? "tcp"))
                .OrderBy(g => g.Key.PublicPort)
                .Select(g =>
                {
                    var ip = g.Select(p => p.Ip).FirstOrDefault(ip => ip is { Length: > 0 } and not ("0.0.0.0" or "::"));
                    return $"{(ip is null ? "" : ip + ":")}{g.Key.PublicPort}→{g.Key.PrivatePort}/{g.Key.Type}";
                })];

    /// <summary>
    /// One badge for a list row: a running container's health says more than "Running", so it
    /// takes its place ("Healthy", "Unhealthy", "Starting"); every other state keeps its own badge.
    /// </summary>
    public static StatusBadge Combined(StatusBadge state, StatusBadge? health) =>
        health is null || state.Label != "Running" ? state
        : health.Label.StartsWith("Health:", StringComparison.Ordinal) ? health with { Label = "Starting" }
        : health;

    /// <summary>
    /// The part of a list item's status text the badge doesn't already say: "Up 45 hours" for a
    /// running one, "18 months ago" for an exited one, empty when there's nothing more.
    /// </summary>
    public static string Since(string? status)
    {
        var text = Parenthesized().Replace(status ?? "", "").Trim();
        if (text.StartsWith("Up ", StringComparison.Ordinal))
            return text;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && text.EndsWith(" ago", StringComparison.Ordinal) ? text[(space + 1)..] : "";
    }

    /// <summary>
    /// Published ports, short: "8080" when the host port is the container's, "8080→80" when it
    /// isn't, "/udp" only for UDP, and the address only when it's bound to one.
    /// </summary>
    public static IReadOnlyList<string> CompactPorts(IReadOnlyList<ContainerPort>? ports) =>
        ports is null
            ? []
            : [.. ports.Where(p => p.PublicPort > 0)
                .GroupBy(p => (p.PublicPort, p.PrivatePort, Type: p.Type ?? "tcp"))
                .OrderBy(g => g.Key.PublicPort)
                .Select(g =>
                {
                    var ip = g.Select(p => p.Ip).FirstOrDefault(ip => ip is { Length: > 0 } and not ("0.0.0.0" or "::"));
                    var mapping = g.Key.PublicPort == g.Key.PrivatePort
                        ? g.Key.PublicPort.ToString(CultureInfo.InvariantCulture)
                        : $"{g.Key.PublicPort}→{g.Key.PrivatePort}";
                    return $"{(ip is null ? "" : ip + ":")}{mapping}{(g.Key.Type == "tcp" ? "" : "/" + g.Key.Type)}";
                })];

    [GeneratedRegex(@"Exited \((\d+)\)")]
    private static partial Regex ExitCode();

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex Parenthesized();
}
