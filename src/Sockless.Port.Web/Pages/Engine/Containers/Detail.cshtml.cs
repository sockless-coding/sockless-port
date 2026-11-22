using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

/// <summary>A container's overview, and the handler for every action taken from its pages.</summary>
public sealed partial class DetailModel(EngineConnections connections, RegistryCredentials registries, ImageUpdates updates, ImageScans scans)
    : ContainerPageModel(connections)
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
    public UpdateStatus? ImageUpdate => updates.ForContainer(EndpointId, Container?.Image, Container?.Config?.Image);

    /// <summary>The vulnerability scan of the container's image.</summary>
    public ScanFlag ImageScan { get; private set; } = ScanFlag.None;

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadScanAsync(cancellationToken);

    /// <summary>Queues a vulnerability scan of the container's image.</summary>
    public IActionResult OnPostScanImage()
    {
        if (Container?.Image is not { } imageId)
        {
            Flash.Error(TempData, "The container's image couldn't be read from the engine.");
            return BackTo(null);
        }
        if (scans.Enqueue(Connection, imageId))
            Flash.Success(TempData, $"Queued a scan of {DisplayName}'s image. The result appears here when it ends.");
        else
            Flash.Error(TempData, $"{DisplayName}'s image is already queued or being scanned, or too many scans are waiting.");
        return BackTo(null);
    }

    private async Task LoadScanAsync(CancellationToken cancellationToken)
    {
        if (Container?.Image is not { } imageId)
            return;
        var stored = await scans.SummariesAsync([imageId], cancellationToken);
        ImageScan = new ScanFlag(stored.GetValueOrDefault(imageId), scans.StateOf(imageId));
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public async Task<IActionResult> OnGetOverviewAsync(CancellationToken cancellationToken)
    {
        await LoadScanAsync(cancellationToken);
        return Partial("_ContainerOverview", this);
    }

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

    /// <summary>Compares the container's image with its registry. Nothing is pulled.</summary>
    public async Task<IActionResult> OnPostCheckUpdateAsync()
    {
        UpdateStatus? status = null;
        var ok = await TryEngineAsync(async client => status = await updates.CheckContainerAsync(Connection, client, ContainerId, HttpContext.RequestAborted));
        if (ok)
        {
            Flash.Success(TempData, status switch
            {
                null => $"{DisplayName} has no image reference to check.",
                UpToDate => $"{DisplayName} runs the image its registry serves now.",
                UpdateAvailable => $"The registry has a newer image for {DisplayName}.",
                UpdateUnknown u => $"Couldn't check {DisplayName}'s image: {u.Reason}",
            });
        }
        return BackTo(null);
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
