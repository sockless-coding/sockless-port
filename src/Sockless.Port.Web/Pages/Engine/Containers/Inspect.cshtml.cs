using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class InspectModel(EngineConnections connections) : ContainerPageModel(connections)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string? Pretty => InspectJson is null ? null : JsonNode.Parse(InspectJson)?.ToJsonString(Indented);

    public void OnGet()
    {
    }
}
