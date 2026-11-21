using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class ConsoleModel(EngineConnections connections) : ContainerPageModel(connections)
{
    public static readonly IReadOnlyList<string> Shells = ["/bin/sh", "/bin/bash", "/bin/ash", "/bin/zsh"];

    public void OnGet()
    {
    }
}
