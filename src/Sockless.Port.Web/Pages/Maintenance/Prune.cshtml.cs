using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Maintenance;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Maintenance;

/// <summary>What an endpoint's scheduled prune removes, and how often.</summary>
public sealed class PruneModel(PruneSchedules prunes, EngineConnections connections) : PageModel
{
    [BindProperty]
    public int IntervalHours { get; set; } = 7 * 24;

    [BindProperty]
    public PruneImages Images { get; set; } = PruneImages.Dangling;

    [BindProperty]
    public int OlderThanDays { get; set; }

    [BindProperty]
    public PruneVolumes Volumes { get; set; }

    [BindProperty]
    public bool Networks { get; set; }

    [BindProperty]
    public bool BuildCache { get; set; }

    public EngineConnection Connection { get; private set; } = null!;

    public bool IsNew { get; private set; }

    /// <summary>False when the engine is online and its API can't keep named volumes (below 1.42).</summary>
    public bool CanPruneAnonymousOnly => Connection.Client?.CanPruneAnonymousVolumesOnly != false;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        if (connections.Get(id) is not { } connection)
            return NotFound();
        Connection = connection;
        if (await prunes.GetAsync(id, cancellationToken) is { } schedule)
        {
            IntervalHours = schedule.IntervalHours;
            Images = schedule.Images;
            OlderThanDays = schedule.OlderThanDays;
            Volumes = schedule.Volumes;
            Networks = schedule.Networks;
            BuildCache = schedule.BuildCache;
        }
        else
        {
            IsNew = true;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        if (connections.Get(id) is not { } connection)
            return NotFound();
        Connection = connection;
        AuditTrail.Action(HttpContext, "Maintenance: change prune schedule");
        AuditTrail.Target(HttpContext, connection.Name);
        var options = new PruneOptions(Images, OlderThanDays, Volumes, Networks, BuildCache);
        var errors = await prunes.SaveAsync(id, IntervalHours, options, cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }
        Flash.Success(TempData, IntervalHours == 0 || options.IsEmpty
            ? $"Turned off the scheduled prune of {connection.Name}."
            : $"{connection.Name} is pruned {(IntervalHours == 24 ? "every day" : "every week")}: {options.Describe()}.");
        return RedirectToPage("/Maintenance/Index");
    }
}
