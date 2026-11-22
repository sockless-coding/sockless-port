using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Volumes;

/// <summary>A volume's overview, and the handler for removing it.</summary>
public sealed class DetailModel(EngineConnections connections, VolumeBrowser browser) : VolumePageModel(connections, browser)
{
    /// <summary>The volume's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Volumes/Detail", "Overview"),
        ("files", "/Engine/Volumes/Files", "Files"),
        ("inspect", "/Engine/Volumes/Inspect", "Inspect"),
    ];

    /// <summary>Bytes on disk, once <see cref="OnGetSizeAsync"/> measured them.</summary>
    public long? Size { get; private set; }

    public string? SizeError { get; private set; }

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public IActionResult OnGetOverview() => Partial("_VolumeOverview", this);

    /// <summary>The size, loaded after the page: the engine measures every volume to answer.</summary>
    public async Task<IActionResult> OnGetSizeAsync(CancellationToken cancellationToken)
    {
        if (Client is { } client && Volume is not null)
        {
            try
            {
                var usage = (await client.GetVolumeUsageAsync(cancellationToken)).FirstOrDefault(v => v.Name == VolumeName)?.UsageData;
                Size = usage?.Size is >= 0 and var size ? size : null;
                if (Size is null)
                    SizeError = "The driver doesn't report a size.";
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                SizeError = EngineFailures.Describe(ex, Connection);
            }
        }
        return Partial("_VolumeSize", this);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? tab)
    {
        var name = ShortName(VolumeName);
        var ok = await TryEngineAsync(async client =>
        {
            // An open file browser mounts the volume, which would block the removal.
            await Browser.ReleaseAsync(Connection, client, VolumeName, HttpContext.RequestAborted);
            await client.RemoveVolumeAsync(VolumeName, cancellationToken: HttpContext.RequestAborted);
        }, $"Removed the volume {name}.");
        return ok ? RedirectToPage("/Engine/Volumes/Index", new { endpointId = EndpointId }) : BackTo(tab);
    }

    private RedirectToPageResult BackTo(string? tab)
    {
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Volumes/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, volumeName = VolumeName });
    }
}
