using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for an image's pages (<c>/endpoints/{endpointId}/images/{imageId}/…</c>): inspects the
/// image and finds the containers using it, once per request. Unknown images are 404; an offline
/// engine renders the page with <see cref="Image"/> null so it can say why.
/// </summary>
public abstract partial class ImagePageModel(EngineConnections connections, ImageUpdates updates) : EndpointPageModel(connections)
{
    public override string? AuditTarget => DisplayName;

    public const string RouteKey = "imageId";

    /// <summary>The ID from the route: 12 to 64 hex characters, without <c>sha256:</c>.</summary>
    public string ImageId { get; private set; } = "";

    public ImageInspect? Image { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>Containers created from this image, running or not.</summary>
    public IReadOnlyList<ContainerSummary> UsedBy { get; private set; } = [];

    /// <summary>Set when the engine is online but inspecting failed.</summary>
    public string? Error { get; private set; }

    public IReadOnlyList<string> Tags => Image?.RepoTags?.Where(t => t != "<none>:<none>").ToList() ?? [];

    public string ShortId => ImageActions.ShortId(Image?.Id ?? ImageId);

    /// <summary>The first tag, or the short ID for an untagged image.</summary>
    public string DisplayName => Tags.Count > 0 ? Tags[0] : ShortId;

    protected ImageUpdates Updates => updates;

    public UpdateStatus? UpdateFor(string tag) => Image is null ? null : updates.Get(EndpointId, Image.Id, tag);

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        ImageId = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!ImageIdPattern().IsMatch(ImageId))
            return NotFound();

        if (Client is not { } client)
            return null;
        try
        {
            (Image, InspectJson) = await client.InspectImageAsync(ImageId, cancellationToken);
            UsedBy = [.. (await client.ListContainersAsync(all: true, cancellationToken)).Where(c => c.ImageId == Image.Id).OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return NotFound();
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
        return null;
    }

    /// <summary>The route value for an image: its hex ID without the <c>sha256:</c> prefix.</summary>
    public static string RouteId(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;

    [GeneratedRegex("^[a-f0-9]{12,64}$")]
    private static partial Regex ImageIdPattern();
}
