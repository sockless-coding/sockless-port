using Sockless.Port.Engines;
using Sockless.Port.Volumes;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Volumes;

public sealed class InspectModel(EngineConnections connections, VolumeBrowser browser) : VolumePageModel(connections, browser)
{
    public string? Pretty => Format.Json(InspectJson);

    public void OnGet()
    {
    }
}
