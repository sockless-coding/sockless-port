using System.Net;
using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>The containers and stacks lists across endpoints, against an engine address where nothing listens.</summary>
public sealed class AllEndpointsPagesTests : IDisposable
{
    private const string Password = "another long password";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private int Local => _factory.LocalEndpoint.Id;

    [Theory]
    [InlineData("/containers", "All containers")]
    [InlineData("/stacks", "All stacks")]
    public async Task Lists_name_an_offline_endpoint_and_watch_it(string path, string title)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var html = await browser.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains($">{title}</h2>", html);
        Assert.Contains($"href=\"{path}\"", html);                    // in the sidebar
        Assert.Contains("be read now, so the list leaves out what runs there", html);
        Assert.Contains("Offline: ", html);
        Assert.Contains($"data-watch-endpoint=\"{Local}\"", html);
        Assert.Contains($"endpoint-changed-{Local} from:body", html);
        Assert.Contains($"<option value=\"{Local}\">local</option>", html);
    }

    [Theory]
    [InlineData("/containers?handler=Table", "id=\"all-containers\"")]
    [InlineData("/stacks?handler=Table", "id=\"all-stacks\"")]
    public async Task Tables_render_without_the_layout(string path, string id)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains(id, html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Endpoints_without_a_role_are_not_listed()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None);
        var user = await _factory.SignInAsync("ann", Password);

        var containers = await user.GetStringAsync("/containers", TestContext.Current.CancellationToken);
        var stacks = await user.GetStringAsync("/stacks", TestContext.Current.CancellationToken);

        Assert.Contains("No endpoints for you yet", containers);
        Assert.Contains("No endpoints for you yet", stacks);
        Assert.DoesNotContain("data-watch-endpoint", containers);
    }

    [Fact]
    public async Task Bulk_action_on_an_offline_endpoint_changes_nothing_and_is_audited()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var html = await PostBulkAsync(browser, "stop", $"{Local}:abc");

        Assert.Contains("local is offline, so nothing was changed there.", html);
        var audit = await browser.GetStringAsync("/audit?outcome=Failed", TestContext.Current.CancellationToken);
        Assert.Contains("Containers: stop", audit);
    }

    [Fact]
    public async Task Bulk_action_on_an_unknown_or_hidden_endpoint_is_refused()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None);
        var user = await _factory.SignInAsync("ann", Password);

        Assert.Contains("1 on an unknown endpoint.", await PostBulkAsync(admin, "stop", "999:abc"));
        Assert.Contains("1 on an unknown endpoint.", await PostBulkAsync(user, "stop", $"{Local}:abc"));
    }

    [Fact]
    public async Task A_viewers_bulk_action_is_refused_and_audited()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None, new Dictionary<int, EndpointRole> { [Local] = EndpointRole.Viewer });
        var viewer = await _factory.SignInAsync("ann", Password);

        var html = await PostBulkAsync(viewer, "stop", $"{Local}:abc");

        Assert.Contains("Needs the operator role on local.", html);
        var audit = await admin.GetStringAsync("/audit?outcome=Denied", TestContext.Current.CancellationToken);
        Assert.Contains("Containers: stop", audit);
        Assert.Contains("ann", audit);
    }

    [Fact]
    public async Task Bulk_action_needs_a_selection()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        Assert.Contains("Select one or more containers first.", await PostBulkAsync(browser, "stop"));
    }

    /// <summary>Posts to the list's bulk handler and returns the page it redirects to.</summary>
    private static async Task<string> PostBulkAsync(HttpClient browser, string action, params string[] ids)
    {
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/containers");
        var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token), new("action", action) };
        fields.AddRange(ids.Select(id => new KeyValuePair<string, string>("ids", id)));
        var post = await browser.PostAsync("/containers?handler=Bulk", new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        return await browser.GetStringAsync(post.Headers.Location!.OriginalString, TestContext.Current.CancellationToken);
    }
}
