using Sockless.Port.Access;
using Sockless.Port.Docker.Engine;

namespace Sockless.Port.Web.Infrastructure;

public enum NavScope
{
    /// <summary>A page that doesn't belong to an endpoint.</summary>
    Global,

    /// <summary>A page under <c>/endpoints/{endpointId}</c>; links go to the current endpoint.</summary>
    Endpoint,
}

/// <summary>A sidebar entry. <see cref="Page"/> is the Razor page path, e.g. <c>/Engine/Index</c>.</summary>
/// <param name="MatchSubPages">Also highlight on pages in the same folder (e.g. a detail page).</param>
/// <param name="Requires">Shown only while the current endpoint is online with these capabilities.</param>
/// <param name="AdminOnly">Shown only to administrators.</param>
/// <param name="Needs">Shown only to users with this permission on the current endpoint (pages that only make sense for changing things).</param>
public sealed record NavItem(string Label, string Page, string Icon, NavScope Scope = NavScope.Global, bool MatchSubPages = true,
    EngineCapabilities Requires = EngineCapabilities.None, bool AdminOnly = false, EndpointPermission Needs = EndpointPermission.View)
{
    public bool IsAvailable(EngineDescriptor? engine) => Requires == EngineCapabilities.None || engine?.Has(Requires) == true;
}

public static class Navigation
{
    /// <summary>Main navigation. Pages are added here as each phase ships them.</summary>
    public static IReadOnlyList<NavItem> Main { get; } =
    [
        new("Overview", "/Overview/Index", "presentation-chart-bar"),
        new("All stacks", "/All/Stacks", "rectangle-stack"),
        new("All containers", "/All/Containers", "cube"),
        new("Dashboard", "/Engine/Index", "home", NavScope.Endpoint, MatchSubPages: false),
        new("Stacks", "/Engine/Stacks/Index", "rectangle-stack", NavScope.Endpoint),
        new("Templates", "/Engine/Templates/Index", "squares-plus", NavScope.Endpoint, Needs: EndpointPermission.Operate),
        new("Containers", "/Engine/Containers/Index", "cube", NavScope.Endpoint),
        new("Resources", "/Engine/Resources/Index", "chart-bar", NavScope.Endpoint),
        new("Pods", "/Engine/Pods/Index", "rectangle-group", NavScope.Endpoint, Requires: EngineCapabilities.Pods),
        new("Images", "/Engine/Images/Index", "square-3-stack-3d", NavScope.Endpoint),
        new("Volumes", "/Engine/Volumes/Index", "circle-stack", NavScope.Endpoint),
        new("Networks", "/Engine/Networks/Index", "share", NavScope.Endpoint),
    ];

    /// <summary>The current endpoint's swarm, shown under their own heading on swarm engines.</summary>
    public static IReadOnlyList<NavItem> Swarm { get; } =
    [
        new("Cluster", "/Engine/Swarm/Index", "globe-alt", NavScope.Endpoint, MatchSubPages: false, Requires: EngineCapabilities.Swarm),
        new("Services", "/Engine/Swarm/Services/Index", "squares-2x2", NavScope.Endpoint, Requires: EngineCapabilities.SwarmManager),
        new("Nodes", "/Engine/Swarm/Nodes/Index", "server", NavScope.Endpoint, Requires: EngineCapabilities.SwarmManager),
        new("Secrets", "/Engine/Swarm/Secrets/Index", "lock-closed", NavScope.Endpoint, Requires: EngineCapabilities.SwarmManager),
        new("Configs", "/Engine/Swarm/Configs/Index", "document-text", NavScope.Endpoint, Requires: EngineCapabilities.SwarmManager),
    ];

    /// <summary>Rarely used administration items, shown below a divider.</summary>
    public static IReadOnlyList<NavItem> Admin { get; } =
    [
        new("Endpoints", "/Endpoints/Index", "server-stack"),
        new("Registries", "/Registries/Index", "key", AdminOnly: true),
        new("Template sources", "/TemplateSources/Index", "squares-plus", AdminOnly: true),
        new("Notifications", "/Notifications/Index", "bell", AdminOnly: true),
        new("Users", "/Users/Index", "users", AdminOnly: true),
        new("Maintenance", "/Maintenance/Index", "wrench-screwdriver", AdminOnly: true),
        new("Audit log", "/Audit/Index", "clipboard-document-list", AdminOnly: true),
        new("Settings", "/Settings/Index", "cog-6-tooth", AdminOnly: true),
    ];

    /// <summary>
    /// True when <paramref name="currentPage"/> is <paramref name="item"/> or, if the item matches
    /// sub-pages, another page in its folder (<c>/Containers/Detail</c> under <c>/Containers/Index</c>).
    /// </summary>
    public static bool IsActive(NavItem item, string? currentPage)
    {
        if (currentPage is null)
            return false;
        if (string.Equals(item.Page, currentPage, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!item.MatchSubPages)
            return false;

        var folder = item.Page.EndsWith("/Index", StringComparison.OrdinalIgnoreCase) ? item.Page[..^"/Index".Length] : item.Page;
        return folder.Length > 0 && currentPage.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
