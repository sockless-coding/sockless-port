using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

/// <summary>A service's tasks and settings, and the handlers for its actions.</summary>
public sealed class DetailModel(EngineConnections connections, SwarmServices services) : ServicePageModel(connections)
{
    /// <summary>The service's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Swarm/Services/Detail", "Overview"),
        ("edit", "/Engine/Swarm/Services/Edit", "Edit"),
        ("logs", "/Engine/Swarm/Services/Logs", "Logs"),
        ("inspect", "/Engine/Swarm/Services/Inspect", "Inspect"),
    ];

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview: on engine events, and every few seconds while it settles.</summary>
    public IActionResult OnGetOverview() => Partial("_ServiceOverview", this);

    public async Task<IActionResult> OnPostScaleAsync(int? replicas)
    {
        if (replicas is not { } count || count < 0 || count > 10_000)
        {
            Flash.Error(TempData, "Enter 0 to 10,000 replicas.");
            return Back();
        }
        await WriteAsync(client => services.ScaleAsync(client, ServiceId, (ulong)count, HttpContext.RequestAborted),
            count == 0 ? $"Scaled {DisplayName} to 0: its tasks stop." : $"Scaling {DisplayName} to {count} {(count == 1 ? "replica" : "replicas")}.");
        return Back();
    }

    /// <param name="pull">Resolve the image's tag again, so tasks start from what it points at now.</param>
    public async Task<IActionResult> OnPostRedeployAsync(bool pull)
    {
        await WriteAsync(client => services.RedeployAsync(client, ServiceId, pull, HttpContext.RequestAborted),
            pull ? $"Replacing the tasks of {DisplayName} with the image its tag points at now." : $"Replacing the tasks of {DisplayName}.");
        return Back();
    }

    public async Task<IActionResult> OnPostRollbackAsync()
    {
        await WriteAsync(client => services.RollbackAsync(client, ServiceId, HttpContext.RequestAborted), $"Rolling {DisplayName} back to its previous settings.");
        return Back();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? tab)
    {
        var ok = await TryEngineAsync(client => client.RemoveServiceAsync(ServiceId, HttpContext.RequestAborted), $"Removed the service {DisplayName}. Its tasks stop on every node.");
        if (ok)
            return RedirectToPage("/Engine/Swarm/Services/Index", new { endpointId = EndpointId });
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Swarm/Services/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, serviceId = ServiceId });
    }

    private async Task WriteAsync(Func<DockerClient, Task<ServiceWriteResult>> write, string success)
    {
        if (Manager is not { } client)
        {
            Flash.Error(TempData, "Services can only be changed through a swarm manager.");
            return;
        }
        try
        {
            var result = await write(client);
            Flash.Success(TempData, result.Warnings.Count == 0 ? success : $"{success} {string.Join(" ", result.Warnings)}");
        }
        catch (ServiceRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, HttpContext.RequestAborted))
        {
            Flash.Error(TempData, EngineFailures.Describe(ex, Connection));
        }
    }

    private RedirectToPageResult Back() => RedirectToPage("/Engine/Swarm/Services/Detail", new { endpointId = EndpointId, serviceId = ServiceId });
}
