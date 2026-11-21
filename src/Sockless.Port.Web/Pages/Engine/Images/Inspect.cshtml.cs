using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

public sealed class InspectModel(EngineConnections connections, ImageUpdates updates) : ImagePageModel(connections, updates)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string? Pretty => InspectJson is null ? null : JsonNode.Parse(InspectJson)?.ToJsonString(Indented);

    public void OnGet()
    {
    }
}
