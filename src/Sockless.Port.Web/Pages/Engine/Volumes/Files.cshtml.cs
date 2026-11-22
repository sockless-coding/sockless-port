using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Volumes;

/// <summary>
/// A volume's files, read through <see cref="VolumeBrowser"/>. Opening the browser starts a
/// helper container, so it's a POST; reading and downloading only use the open one.
/// </summary>
public sealed class FilesModel(EngineConnections connections, VolumeBrowser browser) : VolumePageModel(connections, browser)
{
    /// <summary>The path from the query, normalized; null when it leaves the volume.</summary>
    public string? CurrentPath { get; private set; } = "/";

    public VolumeListing? Listing { get; private set; }

    /// <summary>Why the listing is missing (other than the browser being closed).</summary>
    public string? BrowseError { get; private set; }

    /// <summary>No helper is open: show the button that opens one.</summary>
    public bool Closed { get; private set; }

    public string HelperImage => Browser.HelperImage;

    public async Task<IActionResult> OnGetAsync(string? path, CancellationToken cancellationToken)
    {
        CurrentPath = VolumePath.Normalize(path);
        if (Client is not { } client || Volume is null)
            return Page();
        if (CurrentPath is null)
        {
            BrowseError = "That path leaves the volume.";
            return Page();
        }
        if (!BrowserOpen)
        {
            Closed = true;
            return Page();
        }
        try
        {
            Listing = await Browser.ListAsync(Connection, client, VolumeName, CurrentPath, cancellationToken);
        }
        catch (VolumeBrowserClosedException)
        {
            Closed = true;
        }
        catch (VolumeBrowseException ex)
        {
            BrowseError = ex.Message;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            BrowseError = EngineFailures.Describe(ex, Connection);
        }
        return Page();
    }

    /// <summary>Starts the helper (pulling its image if needed) and shows the path.</summary>
    public async Task<IActionResult> OnPostOpenAsync(string? path, CancellationToken cancellationToken)
    {
        var target = VolumePath.Normalize(path) ?? "/";
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so the file browser can't start.");
            return ToPath(target);
        }
        try
        {
            await Browser.OpenAsync(Connection, client, VolumeName, cancellationToken);
        }
        catch (VolumeBrowseException ex)
        {
            Flash.Error(TempData, ex.Message);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Flash.Error(TempData, $"The file browser couldn't start: {EngineFailures.Describe(ex, Connection)}");
        }
        return ToPath(target);
    }

    public async Task<IActionResult> OnPostCloseAsync(CancellationToken cancellationToken)
    {
        await TryEngineAsync(client => Browser.ReleaseAsync(Connection, client, VolumeName, cancellationToken), "Closed the file browser and removed its helper container.");
        return RedirectToPage("/Engine/Volumes/Detail", new { endpointId = EndpointId, volumeName = VolumeName });
    }

    /// <summary>One regular file, as it is.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(string? path, CancellationToken cancellationToken)
    {
        var target = VolumePath.Normalize(path);
        if (target is null or "/" || Client is not { } client)
            return NotFound();
        try
        {
            await Browser.DownloadFileAsync(Connection, client, VolumeName, target, async (entry, data) =>
            {
                Response.ContentType = "application/octet-stream";
                Response.ContentLength = entry.Size;
                Response.Headers.ContentDisposition = Attachment(entry.Name);
                await data.CopyToAsync(Response.Body, cancellationToken);
            }, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex) when (!Response.HasStarted && (ex is VolumeBrowseException || EngineFailures.IsFailure(ex, cancellationToken)))
        {
            Flash.Error(TempData, ex is VolumeBrowseException ? ex.Message : EngineFailures.Describe(ex, Connection));
            return ToPath(VolumePath.Parent(target) ?? "/");
        }
    }

    /// <summary>A directory (or anything) as a tar archive, as <c>docker cp</c> makes it.</summary>
    public async Task<IActionResult> OnGetArchiveAsync(string? path, CancellationToken cancellationToken)
    {
        var target = VolumePath.Normalize(path);
        if (target is null || Client is not { } client)
            return NotFound();
        var name = target == "/" ? ShortName(VolumeName) : VolumePath.Name(target);
        try
        {
            await Browser.DownloadArchiveAsync(Connection, client, VolumeName, target, async archive =>
            {
                Response.ContentType = "application/x-tar";
                Response.Headers.ContentDisposition = Attachment(name + ".tar");
                await archive.CopyToAsync(Response.Body, cancellationToken);
            }, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex) when (!Response.HasStarted && (ex is VolumeBrowseException || EngineFailures.IsFailure(ex, cancellationToken)))
        {
            Flash.Error(TempData, ex is VolumeBrowseException ? ex.Message : EngineFailures.Describe(ex, Connection));
            return ToPath(target);
        }
    }

    private RedirectToPageResult ToPath(string path) =>
        RedirectToPage("/Engine/Volumes/Files", new { endpointId = EndpointId, volumeName = VolumeName, path = path == "/" ? null : path });

    /// <summary>An attachment header that survives any file name: an ASCII fallback plus the UTF-8 name.</summary>
    private static string Attachment(string fileName)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
            ascii.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_');
        return new ContentDispositionHeaderValue("attachment") { FileName = ascii.ToString(), FileNameStar = fileName }.ToString();
    }
}
