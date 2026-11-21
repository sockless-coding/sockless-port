using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

/// <summary>An image's overview, and the handler for every action taken from its pages.</summary>
public sealed class DetailModel(EngineConnections connections, ImageUpdates updates, RegistryCredentials registries) : ImagePageModel(connections, updates)
{
    /// <summary>The image's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Images/Detail", "Overview"),
        ("history", "/Engine/Images/History", "History"),
        ("inspect", "/Engine/Images/Inspect", "Inspect"),
    ];

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public IActionResult OnGetOverview() => Partial("_ImageOverview", this);

    /// <summary>Adds a tag: <c>name</c> or <c>name:tag</c>.</summary>
    public async Task<IActionResult> OnPostTagAsync(string? reference, string? tab)
    {
        reference = reference?.Trim();
        if (ImageReference.Parse(reference) is not { Digest: null } || Image is null)
        {
            Flash.Error(TempData, "Enter a name such as app:1.2 or registry.example.com/team/app:1.2 (no digest).");
            return BackTo(tab);
        }
        var (name, tag) = ImageReference.Split(reference!);
        await TryEngineAsync(client => client.TagImageAsync(Image.Id, name, tag ?? "latest", HttpContext.RequestAborted), $"Tagged {DisplayName} as {name}:{tag}.");
        return BackTo(tab);
    }

    /// <summary>Removes one tag. The image goes with its last tag, unless containers use it.</summary>
    public async Task<IActionResult> OnPostUntagAsync(string? tag, string? tab)
    {
        if (string.IsNullOrEmpty(tag) || !Tags.Contains(tag))
        {
            Flash.Error(TempData, "That tag isn't on this image any more.");
            return BackTo(tab);
        }
        ImageRemoval? result = null;
        var ok = await TryEngineAsync(async client => result = ImageActions.Summarize(await client.RemoveImageAsync(tag, cancellationToken: HttpContext.RequestAborted)));
        if (!ok)
            return BackTo(tab);
        if (result!.Deleted.Count > 0)
        {
            Flash.Success(TempData, $"Removed {tag}. It was the last tag, so the image was removed too.");
            return RedirectToPage("/Engine/Images/Index", new { endpointId = EndpointId });
        }
        Flash.Success(TempData, $"Removed the tag {tag}.");
        return BackTo(tab);
    }

    public async Task<IActionResult> OnPostRemoveAsync(bool force, string? tab)
    {
        var ok = await TryEngineAsync(client => ImageActions.RemoveAsync(client, ImageId, force, HttpContext.RequestAborted), $"Removed {DisplayName}.");
        return ok ? RedirectToPage("/Engine/Images/Index", new { endpointId = EndpointId }) : BackTo(tab);
    }

    /// <summary>Pulls a tag again and waits: the fallback without scripts.</summary>
    public async Task<IActionResult> OnPostPullAsync(string? tag, string? tab)
    {
        if (string.IsNullOrEmpty(tag) || !Tags.Contains(tag))
            return BackTo(tab);
        string? newId = null;
        var ok = await TryEngineAsync(async client =>
        {
            var auth = await registries.AuthHeaderForAsync(tag, HttpContext.RequestAborted);
            await foreach (var _ in client.PullImageAsync(tag, auth, HttpContext.RequestAborted)) { }
            newId = (await client.InspectImageAsync(tag, HttpContext.RequestAborted)).Image.Id;
        }, $"Pulled {tag}.");
        // A newer image moves the tag to a new ID: follow it.
        return ok && newId is not null && RouteId(newId) != ImageId
            ? RedirectToPage("/Engine/Images/Detail", new { endpointId = EndpointId, imageId = RouteId(newId) })
            : BackTo(tab);
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync(string? tab)
    {
        if (Image is null || Tags.Count == 0)
        {
            Flash.Error(TempData, "An untagged image can't be compared with a registry.");
            return BackTo(tab);
        }
        var found = 0;
        var ok = await TryEngineAsync(async client =>
        {
            foreach (var tag in Tags)
            {
                if (await Updates.CheckAsync(Connection, client, Image.Id, Image.RepoDigests, tag, HttpContext.RequestAborted) is UpdateAvailable)
                    found++;
            }
        });
        if (ok)
            Flash.Success(TempData, found == 0 ? $"Checked {Tags.Count} {(Tags.Count == 1 ? "tag" : "tags")}. See each tag for the result." : $"{found} {(found == 1 ? "tag has" : "tags have")} a newer image in the registry.");
        return BackTo(tab);
    }

    private RedirectToPageResult BackTo(string? tab)
    {
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Images/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, imageId = ImageId });
    }
}
