using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;
using SeverityLevel = Sockless.Port.Vulnerabilities.Severity;

namespace Sockless.Port.Web.Pages.Engine.Images;

/// <summary>An image's known vulnerabilities, from its last scan, and the button to scan it.</summary>
public sealed class VulnerabilitiesModel(EngineConnections connections, ImageUpdates updates, ImageScans scans) : ImagePageModel(connections, updates)
{
    /// <summary>Rows shown at most; filters narrow the rest down.</summary>
    public const int MaxShown = 1000;

    public static readonly IReadOnlyList<(string Value, string Label)> SeverityFilters =
        [("", "All"), ("critical", "Critical"), ("high", "High and worse"), ("medium", "Medium and worse"), ("low", "Low and worse")];

    [BindProperty(SupportsGet = true)]
    public string? Severity { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Fixable { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public ScanResult? Result { get; private set; }

    public ScanState? State { get; private set; }

    /// <summary>The findings that match the filters, worst first.</summary>
    public IReadOnlyList<VulnerabilityFinding> Shown { get; private set; } = [];

    /// <summary>How many match the filters, of which <see cref="Shown"/> holds up to <see cref="MaxShown"/>.</summary>
    public int Matching { get; private set; }

    public bool Filtered => !string.IsNullOrEmpty(Severity) || Fixable || !string.IsNullOrWhiteSpace(Q);

    public object Filters => new { endpointId = EndpointId, imageId = ImageId, Severity, Fixable, Q };

    /// <summary>The full image ID: from the engine, or from the route while it's offline.</summary>
    private string FullId => Image?.Id ?? ImageScans.Normalize(ImageId);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadScanAsync(cancellationToken);

    /// <summary>htmx refresh of the summary while a scan runs, and when it ends.</summary>
    public async Task<IActionResult> OnGetSummaryAsync(CancellationToken cancellationToken)
    {
        await LoadScanAsync(cancellationToken);
        return Partial("_ImageScanSummary", this);
    }

    /// <summary>htmx refresh of the findings, so a finished scan shows up.</summary>
    public async Task<IActionResult> OnGetFindingsAsync(CancellationToken cancellationToken)
    {
        await LoadScanAsync(cancellationToken);
        return Partial("_ImageFindings", this);
    }

    public IActionResult OnPostScan()
    {
        if (Image is null)
        {
            Flash.Error(TempData, Client is null ? "The engine is offline, so the image can't be copied for a scan." : "The image couldn't be read from the engine.");
            return RedirectToPage(Filters);
        }
        if (scans.Enqueue(Connection, Image.Id))
            Flash.Success(TempData, $"Queued a scan of {DisplayName}. The results appear here when it ends.");
        else
            Flash.Error(TempData, $"{DisplayName} is already queued or being scanned, or too many scans are waiting.");
        return RedirectToPage(Filters);
    }

    private async Task LoadScanAsync(CancellationToken cancellationToken)
    {
        State = scans.StateOf(FullId);
        Result = await scans.GetAsync(FullId, cancellationToken);
        if (Result is null)
            return;
        var minimum = Severity is { Length: > 0 } ? Severities.Parse(Severity) : (SeverityLevel?)null;
        var q = Q?.Trim();
        var matching = Result.Findings.Where(f =>
            (minimum is not { } min || f.Severity >= min)
            && (!Fixable || f.Fixable)
            && (string.IsNullOrEmpty(q)
                || f.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || f.Package.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (f.Target?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (f.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        Matching = matching.Count;
        Shown = [.. matching.Take(MaxShown)];
    }
}
