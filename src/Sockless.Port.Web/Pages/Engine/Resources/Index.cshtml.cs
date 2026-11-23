using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Resources;

/// <summary>
/// What the endpoint's running containers use: totals against the host and a live table per
/// container or stack (through <c>EngineHub.StreamResourceUsage</c>), and trends from the
/// recorded history (<see cref="OnGetHistoryAsync"/>), which stay readable while the engine is offline.
/// </summary>
public sealed class IndexModel(EngineConnections connections, ResourceHistory history) : EndpointPageModel(connections)
{
    public bool HistoryEnabled => history.Enabled;

    /// <summary>The periods the setting keeps enough history for.</summary>
    public IReadOnlyList<HistoryRange> Ranges => history.Ranges;

    public static HistoryRange DefaultRange => HistoryRange.Day;

    public static string Label(HistoryRange range) => range switch
    {
        HistoryRange.Hour => "Last hour",
        HistoryRange.Day => "Last 24 hours",
        HistoryRange.Week => "Last 7 days",
        HistoryRange.Month => "Last 30 days",
        _ => "Last 90 days",
    };

    public void OnGet()
    {
    }

    /// <param name="Label">The point's start, short, in the user's zone.</param>
    public sealed record PointJson(string Label, double? Cpu, double? CpuPeak, long? Memory, long? MemoryPeak,
        double? NetworkRx, double? NetworkTx, double? BlockRead, double? BlockWrite);

    /// <param name="Period">For people: the period and what each point covers.</param>
    public sealed record HistoryJson(bool Enabled, string Range, string Period, int Cpus, long HostMemory,
        IReadOnlyList<PointJson> Points, IReadOnlyList<HistoryContainer> Containers);

    /// <summary>The trend for the chosen period, as JSON for <c>trends.js</c>: the endpoint's totals, or one container's use.</summary>
    public async Task<IActionResult> OnGetHistoryAsync(string? range, string? name, CancellationToken cancellationToken)
    {
        var ranges = history.Ranges;
        if (ranges.Count == 0)
            return new JsonResult(new HistoryJson(false, "", "Resource history is off.", 0, 0, [], []));

        var chosen = Enum.TryParse<HistoryRange>(range, ignoreCase: true, out var parsed) && ranges.Contains(parsed) ? parsed
            : ranges.Contains(DefaultRange) ? DefaultRange : ranges[0];
        var view = await history.ReadAsync(EndpointId, chosen, string.IsNullOrWhiteSpace(name) ? null : name, cancellationToken);

        var zone = TimeZones.For(HttpContext);
        var withDate = chosen >= HistoryRange.Week;
        var period = $"{Label(chosen)}, since {Format.Timestamp(view.From, zone)}. Each point is the average over {Span(view.Bucket)}.";
        return new JsonResult(new HistoryJson(true, chosen.ToString(), period, view.Cpus, view.HostMemory,
            [.. view.Points.Select(p => new PointJson(Format.ChartTime(p.Time, withDate, zone), Round(p.Cpu), Round(p.CpuPeak), p.Memory, p.MemoryPeak,
                Round(p.NetworkRx), Round(p.NetworkTx), Round(p.BlockRead), Round(p.BlockWrite)))],
            view.Containers));
    }

    /// <summary><c>a minute</c>, <c>10 minutes</c>, <c>an hour</c>, <c>4 hours</c>.</summary>
    internal static string Span(TimeSpan span) => span.TotalHours >= 1
        ? (span.TotalHours == 1 ? "an hour" : $"{span.TotalHours:0} hours")
        : (span.TotalMinutes == 1 ? "a minute" : $"{span.TotalMinutes:0} minutes");

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;
}
