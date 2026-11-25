using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;
using Sockless.Port.Work;

namespace Sockless.Port.Web.Pages.Engine.Images;

/// <param name="UsedBy">Names of the containers created from the image.</param>
/// <param name="Update">The last update check's result for the image, if it was checked.</param>
/// <param name="Scan">Its vulnerability scan: the stored result and any scan in progress.</param>
public sealed record ImageRow(ImageSummary Summary, IReadOnlyList<string> UsedBy, UpdateStatus? Update, ScanFlag Scan)
{
    public string Id => ImagePageModel.RouteId(Summary.Id);
    public string ShortId => ImageActions.ShortId(Summary.Id);
    public string Name => Summary.Tags.Count > 0 ? Summary.Tags[0] : ShortId;
}

public sealed class IndexModel(EngineConnections connections, ImageJobs jobs, ImageUpdates updates, RegistryCredentials registries, ImageScans scans,
    WorkTracker work, IHostApplicationLifetime stopping)
    : EndpointPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> Usages =
        [("", "All"), ("used", "In use"), ("unused", "Unused"), ("dangling", "Dangling")];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Usage { get; set; }

    public IReadOnlyList<ImageRow> Rows { get; private set; } = [];
    public int Total { get; private set; }
    public long TotalSize { get; private set; }
    public int Dangling { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<ImageJob> Jobs => jobs.ForEndpoint(EndpointId);

    public DateTimeOffset? LastChecked => updates.LastChecked(EndpointId);

    public int PendingScans => scans.PendingFor(EndpointId);

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Usage);

    public object Filters => new { endpointId = EndpointId, Q, Usage };

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Images/Index", Filters);
        return Partial("_ImageTable", this);
    }

    public IActionResult OnGetJobs() => Partial("_ImageJobs", this);

    /// <summary>Pulls and waits: the form's fallback without scripts. With scripts, the pull runs as a job with live progress.</summary>
    public async Task<IActionResult> OnPostPullAsync(string? reference, CancellationToken cancellationToken)
    {
        reference = reference?.Trim();
        if (ImageReference.Parse(reference) is null)
        {
            Flash.Error(TempData, "Enter an image such as nginx, nginx:1.27 or ghcr.io/org/app:tag.");
            return RedirectToPage(Filters);
        }
        await TryEngineAsync(async client =>
        {
            var auth = await registries.AuthHeaderForAsync(reference!, cancellationToken);
            await foreach (var _ in client.PullImageAsync(reference!, auth, cancellationToken)) { }
        }, $"Pulled {reference}.");
        return RedirectToPage(Filters);
    }

    /// <summary>Removes the selected images (the row buttons post a single id).</summary>
    /// <remarks>
    /// Listed among the background tasks with its progress, and it goes on if the browser leaves
    /// (only shutdown stops it): a long removal shouldn't end half done because of a click elsewhere.
    /// </remarks>
    public async Task<IActionResult> OnPostRemoveAsync(string[] ids, bool force, CancellationToken _)
    {
        var cancellationToken = stopping.ApplicationStopping;
        if (ids.Length == 0)
        {
            Flash.Error(TempData, "Select one or more images first.");
            return RedirectToPage(Filters);
        }
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return RedirectToPage(Filters);
        }

        var targets = ids.Distinct().Take(500).ToList();
        using var item = work.Start(WorkKind.RemoveImages, targets.Count == 1 ? "Removing an image" : $"Removing {targets.Count} images", EndpointId, Connection.Name,
            by: User.Identity?.Name);
        var removed = new List<string>();
        var failed = new List<string>();
        long freed = 0;
        var sizes = await SizesAsync(client, cancellationToken);
        foreach (var id in targets)
        {
            var name = await NameOfAsync(client, id, cancellationToken);
            if (targets.Count == 1)
                item.Retitle($"Removing {name}");
            item.Report(removed.Count + failed.Count, targets.Count, WorkUnit.Items, name);
            try
            {
                var result = await ImageActions.RemoveAsync(client, id, force, cancellationToken);
                removed.Add(name);
                if (result.Deleted.Count > 0)
                    freed += sizes.GetValueOrDefault(ImagePageModel.RouteId(id));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{name}: {EngineFailures.Describe(ex, Connection)}");
            }
        }

        string? success = null;
        if (removed.Count > 0)
        {
            success = $"Removed {(removed.Count <= 3 ? string.Join(", ", removed) : $"{removed.Count} images")}{(freed > 0 ? $", freeing about {Format.Bytes(freed)}" : "")}.";
            Flash.Success(TempData, success);
        }
        if (failed.Count > 0)
        {
            Flash.Error(TempData, $"Couldn't remove {string.Join(" ", failed)}");
            item.Fail($"{(success is null ? "" : success + " ")}Couldn't remove {string.Join(" ", failed)}");
        }
        else
        {
            item.Succeed(success ?? "Nothing was removed.");
        }
        return RedirectToPage(Filters);
    }

    /// <param name="all">Every image no container uses, not only dangling ones.</param>
    public async Task<IActionResult> OnPostPruneAsync(bool all, CancellationToken cancellationToken)
    {
        ImagePruneResponse? result = null;
        using var item = work.Start(WorkKind.PruneImages, $"Removing {(all ? "unused" : "dangling")} images", EndpointId, Connection.Name, by: User.Identity?.Name);
        await TryEngineAsync(async client => result = await client.PruneImagesAsync(all, cancellationToken));
        if (result is not null)
        {
            var deleted = ImageActions.Summarize(result.ImagesDeleted);
            var message = deleted.Deleted.Count == 0 && deleted.Untagged.Count == 0
                ? $"Nothing to remove: there are no {(all ? "unused" : "dangling")} images."
                : $"Removed {(all ? "unused" : "dangling")} images: {deleted.Untagged.Count} tags and {deleted.Deleted.Count} images or layers, freeing {Format.Bytes(result.SpaceReclaimed)}.";
            Flash.Success(TempData, message);
            item.Succeed(message);
        }
        else
        {
            item.Fail(Flash.PeekError(TempData) ?? "The engine couldn't remove them.");
        }
        return RedirectToPage(Filters);
    }

    /// <summary>Compares every tag in use with its registry. Takes a few seconds with many images.</summary>
    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateCheckSummary? summary = null;
        await TryEngineAsync(async client => summary = await updates.CheckInUseAsync(Connection, client, cancellationToken));
        if (summary is not null)
        {
            Flash.Success(TempData, Describe(summary));
        }
        return RedirectToPage(Filters);
    }

    /// <summary>Queues a vulnerability scan of every image a container uses, running or not.</summary>
    public async Task<IActionResult> OnPostScanInUseAsync(CancellationToken cancellationToken)
    {
        int queued = 0, skipped = 0;
        var ok = await TryEngineAsync(async client =>
        {
            var inUse = (await client.ListContainersAsync(all: true, cancellationToken)).Select(c => c.ImageId).OfType<string>().Distinct(StringComparer.Ordinal);
            foreach (var imageId in inUse)
            {
                if (scans.Enqueue(Connection, imageId))
                    queued++;
                else
                    skipped++;
            }
        });
        if (ok)
            Flash.Success(TempData, ScanDisplay.QueuedMessage(queued, skipped, "No container uses an image, so there's nothing to scan."));
        return RedirectToPage(Filters);
    }

    /// <summary>"Checked 3 tags in use: 1 with an update available, 2 up to date."</summary>
    internal static string Describe(UpdateCheckSummary summary) =>
        ImageDisplay.CheckMessage(summary, "tag in use", "tags in use", "No image in use has a tag to check.", "the table");

    private async Task<Dictionary<string, long>> SizesAsync(DockerClient client, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.ListImagesAsync(cancellationToken)).ToDictionary(i => ImagePageModel.RouteId(i.Id), i => i.Size, StringComparer.Ordinal);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return [];
        }
    }

    private static async Task<string> NameOfAsync(DockerClient client, string id, CancellationToken cancellationToken)
    {
        try
        {
            var (image, _) = await client.InspectImageAsync(id, cancellationToken);
            return image.RepoTags?.FirstOrDefault(t => t != "<none>:<none>") ?? ImageActions.ShortId(image.Id);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return ImageActions.ShortId(id);
        }
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            var images = await client.ListImagesAsync(cancellationToken);
            var containers = await client.ListContainersAsync(all: true, cancellationToken);
            // Computed here: Docker below API 1.51 reports Containers as -1.
            var usedBy = containers.Where(c => c.ImageId is not null).ToLookup(c => c.ImageId!, c => c.DisplayName, StringComparer.Ordinal);

            Total = images.Count;
            TotalSize = images.Sum(i => i.Size);
            Dangling = images.Count(i => i.IsDangling);
            var scanned = await scans.SummariesAsync(images.Select(i => i.Id), cancellationToken);
            Rows = [.. images
                .Select(i => new ImageRow(i, [.. usedBy[i.Id].Order(StringComparer.OrdinalIgnoreCase)], updates.ForImage(EndpointId, i.Id),
                    new ScanFlag(scanned.GetValueOrDefault(i.Id), scans.StateOf(i.Id))))
                .Where(Matches)
                .OrderBy(r => r.Summary.IsDangling)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private bool Matches(ImageRow row)
    {
        var usage = Usage switch
        {
            "used" => row.UsedBy.Count > 0,
            "unused" => row.UsedBy.Count == 0,
            "dangling" => row.Summary.IsDangling,
            _ => true,
        };
        if (!usage)
            return false;
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return row.Summary.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
               || row.Id.StartsWith(q.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? q[7..] : q, StringComparison.OrdinalIgnoreCase);
    }
}
