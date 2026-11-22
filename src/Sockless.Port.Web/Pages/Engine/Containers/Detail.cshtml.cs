using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

/// <summary>A container's overview, and the handler for every action taken from its pages.</summary>
public sealed partial class DetailModel(EngineConnections connections, RegistryCredentials registries, ImageUpdates updates) : ContainerPageModel(connections)
{
    /// <summary>The container's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Containers/Detail", "Overview"),
        ("logs", "/Engine/Containers/Logs", "Logs"),
        ("stats", "/Engine/Containers/Stats", "Stats"),
        ("console", "/Engine/Containers/Console", "Console"),
        ("inspect", "/Engine/Containers/Inspect", "Inspect"),
    ];

    /// <summary>The last update check's result for the container's image, if it was checked.</summary>
    public UpdateStatus? ImageUpdate => Container?.Image is { } image ? updates.ForImage(EndpointId, image) : null;

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public IActionResult OnGetOverview() => Partial("_ContainerOverview", this);

    public async Task<IActionResult> OnPostActionAsync(string? action, bool removeVolumes, string? tab)
    {
        if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var verb))
            return BadRequest();

        var ok = await TryEngineAsync(client => ContainerActions.RunAsync(client, verb, ContainerId, removeVolumes, HttpContext.RequestAborted),
            $"{ContainerActions.Done(verb)} {DisplayName}.");
        return ok && verb == ContainerAction.Remove
            ? RedirectToPage("/Engine/Containers/Index", new { endpointId = EndpointId })
            : BackTo(tab);
    }

    public async Task<IActionResult> OnPostRenameAsync(string? name, string? tab)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || !NamePattern().IsMatch(name))
        {
            Flash.Error(TempData, "Use letters, digits and . _ - only, starting with a letter or digit.");
            return BackTo(tab);
        }
        await TryEngineAsync(client => client.RenameContainerAsync(ContainerId, name, HttpContext.RequestAborted), $"Renamed {DisplayName} to {name}.");
        return BackTo(tab);
    }

    /// <param name="pulled">images.js already pulled the image with live progress.</param>
    public async Task<IActionResult> OnPostRecreateAsync(bool pull, bool pulled)
    {
        if (SwarmService is not null)
        {
            Flash.Error(TempData, $"This container is a task of the swarm service {SwarmService}. Update the service instead.");
            return BackTo(null);
        }
        if (Pod is not null)
        {
            Flash.Error(TempData, $"{DisplayName} is in the pod {Pod.Pod.Name}, and a recreated container would leave it. Recreate it with podman run --pod {Pod.Pod.Name} instead.");
            return BackTo(null);
        }

        string? newId = null;
        var ok = await TryEngineAsync(async client => newId = await ContainerRecreator.RecreateAsync(client, ContainerId, pull && !pulled, HttpContext.RequestAborted,
                registries.AuthHeaderForAsync),
            pull || pulled ? $"Pulled the image and recreated {DisplayName}." : $"Recreated {DisplayName}.");
        return ok ? RedirectToPage("/Engine/Containers/Detail", new { endpointId = EndpointId, containerId = newId }) : BackTo(null);
    }

    private RedirectToPageResult BackTo(string? tab)
    {
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Containers/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, containerId = ContainerId });
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NamePattern();
}
