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

    [GeneratedRegex(@"Exited \((\d+)\)")]
    private static partial Regex ExitCode();
}
