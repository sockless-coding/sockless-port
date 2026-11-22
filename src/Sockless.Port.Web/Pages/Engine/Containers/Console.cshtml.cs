using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class ConsoleModel(EngineConnections connections) : ContainerPageModel(connections)
{
    /// <summary>A shell in the container: operators only.</summary>
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    public static readonly IReadOnlyList<string> Shells = ["/bin/sh", "/bin/bash", "/bin/ash", "/bin/zsh"];

    public void OnGet()
    {
    }
}
