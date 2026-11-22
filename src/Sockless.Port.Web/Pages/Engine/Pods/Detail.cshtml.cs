using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Pods;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Pods;

/// <summary>A pod's overview, and the handler for its actions.</summary>
public sealed class DetailModel(EngineConnections connections) : PodPageModel(connections)
{
    /// <summary>The pod's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Pods/Detail", "Overview"),
        ("inspect", "/Engine/Pods/Inspect", "Inspect"),
    ];

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public IActionResult OnGetOverview() => Partial("_PodOverview", this);

    public async Task<IActionResult> OnPostActionAsync(string? action, string? tab)
    {
        if (!Enum.TryParse<PodAction>(action, ignoreCase: true, out var verb))
        {
            Flash.Error(TempData, "Unknown action.");
            return Back(tab);
        }
        if (Pod is null)
        {
            Flash.Error(TempData, Error ?? "The pod can't be read, so nothing was changed.");
            return Back(tab);
        }
        if (PodActions.EndsSelf(verb, IsSelf))
        {
            Flash.Error(TempData, $"Sockless Port runs in {DisplayName}, so it can't {verb.ToString().ToLowerInvariant()} it from here.");
            return Back(tab);
        }

        var ok = await TryEngineAsync(client => PodActions.RunAsync(client, verb, Pod.Id, HttpContext.RequestAborted),
            $"{PodActions.Done(verb)} the pod {DisplayName}.");
        return ok && verb == PodAction.Remove ? RedirectToPage("/Engine/Pods/Index", new { endpointId = EndpointId }) : Back(tab);
    }

    private RedirectToPageResult Back(string? tab)
    {
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Pods/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, podId = PodId });
    }
}
