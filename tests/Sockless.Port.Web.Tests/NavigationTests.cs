using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

public sealed class NavigationTests
{
    [Theory]
    [InlineData("/Index", "/Index", true)]
    [InlineData("/Containers/Index", "/Containers/Index", true)]
    [InlineData("/Containers/Index", "/Containers/Detail", true)]
    [InlineData("/Containers/Index", "/ContainersOther/Index", false)]
    [InlineData("/Index", "/Containers/Index", false)]
    [InlineData("/Index", null, false)]
    public void Active_item_matches_its_page_and_sub_pages(string itemPage, string? currentPage, bool expected)
    {
        Assert.Equal(expected, Navigation.IsActive(new NavItem("x", itemPage, "home"), currentPage));
    }

    [Fact]
    public void Items_that_do_not_match_sub_pages_stay_inactive_on_siblings()
    {
        var dashboard = new NavItem("Dashboard", "/Engine/Index", "home", NavScope.Endpoint, MatchSubPages: false);

        Assert.True(Navigation.IsActive(dashboard, "/Engine/Index"));
        Assert.False(Navigation.IsActive(dashboard, "/Engine/Containers/Index"));
    }
}
