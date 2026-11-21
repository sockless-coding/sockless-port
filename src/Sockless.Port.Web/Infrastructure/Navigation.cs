namespace Sockless.Port.Web.Infrastructure;

/// <summary>A sidebar entry. <see cref="Page"/> is the Razor page path, e.g. <c>/Index</c>.</summary>
public sealed record NavItem(string Label, string Page, string Icon);

public static class Navigation
{
    /// <summary>Main navigation. Pages are added here as each phase ships them.</summary>
    public static IReadOnlyList<NavItem> Main { get; } =
    [
        new("Dashboard", "/Index", "home"),
    ];

    /// <summary>Rarely used administration items, shown below a divider.</summary>
    public static IReadOnlyList<NavItem> Admin { get; } = [];

    /// <summary>
    /// True when <paramref name="currentPage"/> is <paramref name="item"/> or one of its sub-pages,
    /// e.g. <c>/Containers/Detail</c> under <c>/Containers/Index</c>.
    /// </summary>
    public static bool IsActive(NavItem item, string? currentPage)
    {
        if (currentPage is null)
            return false;
        if (string.Equals(item.Page, currentPage, StringComparison.OrdinalIgnoreCase))
            return true;

        var folder = item.Page.EndsWith("/Index", StringComparison.OrdinalIgnoreCase) ? item.Page[..^"/Index".Length] : item.Page;
        return folder.Length > 0 && currentPage.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
