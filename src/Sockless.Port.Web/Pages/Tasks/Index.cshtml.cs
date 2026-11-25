using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Web.Infrastructure;
using Sockless.Port.Work;

namespace Sockless.Port.Web.Pages.Tasks;

/// <param name="Items">What the user may see, active first.</param>
public sealed record WorkTrayView(IReadOnlyList<WorkView> Items)
{
    public int Active => Items.Count(i => i.IsActive);

    public static WorkTrayView For(HttpContext context, WorkTracker work) => new(WorkDisplay.VisibleTo(context.Access(), work.Snapshot()));
}

/// <summary>
/// What's going on in the background across endpoints: pulls, builds, stack runs, scans, update
/// checks, removals, prunes and backups, running and recently finished. The top bar's tasks
/// button shows the same list (<see cref="OnGetTray"/>), refreshed when the hub says it changed.
/// </summary>
public sealed class IndexModel(WorkTracker work) : PageModel
{
    public IReadOnlyList<WorkView> Items { get; private set; } = [];

    public int Active => Items.Count(i => i.IsActive);

    public void OnGet() => Items = WorkDisplay.VisibleTo(HttpContext.Access(), work.Snapshot());

    /// <summary>The page's list, again.</summary>
    public IActionResult OnGetList()
    {
        OnGet();
        return Partial("_WorkList", this);
    }

    /// <summary>The top bar's list and the count on its button.</summary>
    public IActionResult OnGetTray() => Partial("_WorkTray", WorkTrayView.For(HttpContext, work));
}
