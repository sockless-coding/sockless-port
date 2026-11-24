using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>The overview across endpoints, against an engine address where nothing listens.</summary>
public sealed class OverviewPagesTests : IDisposable
{
    private const string Password = "another long password";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private int Local => _factory.LocalEndpoint.Id;

    [Fact]
    public async Task Overview_lists_each_endpoint_as_a_row_that_loads_itself()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/overview", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/overview\"", html);                 // in the sidebar
        Assert.Contains($"data-overview-row=\"{Local}\"", html);
        Assert.Contains($"hx-get=\"/overview?id={Local}&amp;handler=Row\"", html);
        Assert.Contains($"hx-trigger=\"load, endpoint-changed-{Local} from:body, every 30s\"", html);
        Assert.Contains("Loading…", html);
        Assert.DoesNotContain("data-endpoint-id=", html);            // not an endpoint's page: it watches its rows' endpoints
    }

    [Fact]
    public async Task An_offline_endpoint_row_says_why()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var html = await browser.GetStringAsync($"/overview?handler=Row&id={Local}", TestContext.Current.CancellationToken);

        Assert.StartsWith("<tr", html.TrimStart());
        Assert.Contains(">Offline<", html);
        Assert.Contains(">Local<", html);
        Assert.Contains("/nonexistent/sockless-port-test.sock", html);
        Assert.Contains($"hx-trigger=\"endpoint-changed-{Local} from:body, every 30s\"", html); // no load: it would loop
        Assert.Contains($"href=\"/endpoints/{Local}\"", html);
    }

    [Fact]
    public async Task An_unknown_endpoint_row_is_empty_so_it_disappears()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/overview?handler=Row&id=999", TestContext.Current.CancellationToken);

        Assert.Equal("", html);
    }

    [Fact]
    public async Task The_table_renders_without_the_layout()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/overview?handler=Table", TestContext.Current.CancellationToken);

        Assert.Contains("id=\"overview\"", html);
        Assert.Contains("hx-trigger=\"overview-changed from:body\"", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Endpoints_without_a_role_are_not_shown()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None);
        var user = await _factory.SignInAsync("ann", Password);

        var html = await user.GetStringAsync("/overview", TestContext.Current.CancellationToken);
        var row = await user.GetStringAsync($"/overview?handler=Row&id={Local}", TestContext.Current.CancellationToken);

        Assert.Contains("No endpoints for you yet", html);
        Assert.DoesNotContain("data-overview-row", html);
        Assert.Equal("", row);
    }

    [Fact]
    public async Task A_viewer_sees_the_endpoint()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None, new Dictionary<int, EndpointRole> { [Local] = EndpointRole.Viewer });
        var user = await _factory.SignInAsync("ann", Password);

        var html = await user.GetStringAsync("/overview", TestContext.Current.CancellationToken);
        var row = await user.GetStringAsync($"/overview?handler=Row&id={Local}", TestContext.Current.CancellationToken);

        Assert.Contains($"data-overview-row=\"{Local}\"", html);
        Assert.Contains("local", row);
    }

    [Theory]
    [InlineData("unhealthy", "Unhealthy")]
    [InlineData("update", "Update available")]
    public void Containers_page_offers_the_filters_the_overview_links_to(string value, string label) =>
        Assert.Contains((value, label), Pages.Engine.Containers.IndexModel.States);
}
