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
public sealed record NavItem(string Label, string Page, string Icon, NavScope Scope = NavScope.Global, bool MatchSubPages = true);

public static class Navigation
{
    /// <summary>Main navigation. Pages are added here as each phase ships them.</summary>
    public static IReadOnlyList<NavItem> Main { get; } =
    [
        new("Dashboard", "/Engine/Index", "home", NavScope.Endpoint, MatchSubPages: false),
        new("Containers", "/Engine/Containers/Index", "cube", NavScope.Endpoint),
    ];

    /// <summary>Rarely used administration items, shown below a divider.</summary>
    public static IReadOnlyList<NavItem> Admin { get; } =
    [
        new("Endpoints", "/Endpoints/Index", "server-stack"),
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
