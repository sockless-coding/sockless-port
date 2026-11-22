using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a volume's pages (<c>/endpoints/{endpointId}/volumes/{volumeName}/…</c>): inspects the
/// volume and finds the containers using it, once per request. Unknown volumes are 404; an
/// offline engine renders the page with <see cref="Volume"/> null so it can say why.
/// </summary>
public abstract class VolumePageModel(EngineConnections connections, VolumeBrowser browser) : EndpointPageModel(connections)
{
    public const string RouteKey = "volumeName";

    public string VolumeName { get; private set; } = "";

    public VolumeSummary? Volume { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>Containers mounting the volume, running or not.</summary>
    public IReadOnlyList<VolumeUse> UsedBy { get; private set; } = [];

    /// <summary>Set when the engine is online but inspecting failed.</summary>
    public string? Error { get; private set; }

    protected VolumeBrowser Browser => browser;

    /// <summary>Whether a file browser helper is open for this volume.</summary>
    public bool BrowserOpen => browser.IsOpen(EndpointId, VolumeName);

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        VolumeName = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!VolumeCreateSpecBuilder.VolumeName().IsMatch(VolumeName))
            return NotFound();

        if (Client is not { } client)
            return null;
        try
        {
            (Volume, InspectJson) = await client.InspectVolumeAsync(VolumeName, cancellationToken);
            UsedBy = [.. VolumeUsage.ByVolume(await client.ListContainersAsync(all: true, cancellationToken))[VolumeName]];
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

    /// <summary>A volume's display name: anonymous ones are known by their first 12 characters.</summary>
    public static string ShortName(string name) => name.Length == 64 ? name[..12] : name;
}
