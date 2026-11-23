using System.Net;

namespace Sockless.Port.Web.Tests;

/// <summary>Volume and network pages with no engine: they must explain, not fail.</summary>
public sealed class VolumeNetworkPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/endpoints/1/volumes")]
    [InlineData("/endpoints/1/volumes/create")]
    [InlineData("/endpoints/1/volumes/data")]
    [InlineData("/endpoints/1/volumes/data/files")]
    [InlineData("/endpoints/1/volumes/data/files?path=/conf")]
    [InlineData("/endpoints/1/volumes/data/inspect")]
    [InlineData("/endpoints/1/networks")]
    [InlineData("/endpoints/1/networks/create")]
    [InlineData("/endpoints/1/networks/backend")]
    [InlineData("/endpoints/1/networks/backend/inspect")]
    public async Task Pages_render_the_offline_explanation(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/endpoints/1/volumes/-data")]
    [InlineData("/endpoints/1/volumes/x")]
    [InlineData("/endpoints/1/volumes/a%20b/files")]
    [InlineData("/endpoints/9/volumes")]
    [InlineData("/endpoints/1/networks/.hidden")]
    [InlineData("/endpoints/9/networks")]
    public async Task Invalid_names_and_endpoints_are_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Volumes_and_networks_appear_in_the_navigation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/networks", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/endpoints/1/volumes\"", html);
        Assert.Contains("href=\"/endpoints/1/networks\"", html);
        // Static assets are fingerprinted: /js/networks.<hash>.js.
        Assert.Matches("""<script src="/js/networks(\.[a-z0-9]+)?\.js" """.TrimEnd(), html);
    }

    [Fact]
    public async Task Actions_while_offline_say_nothing_changed()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/registries");
        var ct = TestContext.Current.CancellationToken;

        foreach (var (action, fields) in new (string, Dictionary<string, string>)[]
                 {
                     ("/endpoints/1/volumes?handler=Remove", new() { ["ids"] = "data" }),
                     ("/endpoints/1/volumes?handler=Prune", new() { ["all"] = "true" }),
                     ("/endpoints/1/volumes/data?handler=Remove", new()),
                     ("/endpoints/1/volumes/data/files?handler=Close", new()),
                     ("/endpoints/1/networks?handler=Remove", new() { ["ids"] = "abc" }),
                     ("/endpoints/1/networks?handler=Prune", new()),
                     ("/endpoints/1/networks/backend?handler=Remove", new()),
                 })
        {
            var post = await browser.PostAsync(action, PortFactory.Form(csrf, fields), ct);
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("The engine is offline, so nothing was changed.", await browser.GetStringAsync(post.Headers.Location!.OriginalString, ct));
        }

        var open = await browser.PostAsync("/endpoints/1/volumes/data/files?handler=Open", PortFactory.Form(csrf, new() { ["path"] = "/conf" }), ct);
        Assert.Equal("/endpoints/1/volumes/data/files?path=%2Fconf", open.Headers.Location!.OriginalString);
        Assert.Contains("The engine is offline, so the file browser can't start.", WebUtility.HtmlDecode(await browser.GetStringAsync(open.Headers.Location.OriginalString, ct)));
    }

    [Fact]
    public async Task A_save_while_offline_keeps_the_edits()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/registries");
        var ct = TestContext.Current.CancellationToken;

        var post = await browser.PostAsync("/endpoints/1/volumes/data/files?handler=Save",
            PortFactory.Form(csrf, new() { ["path"] = "/conf/app.yml", ["content"] = "port: 8080 <edited>", ["version"] = "abc" }), ct);

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var html = WebUtility.HtmlDecode(await post.Content.ReadAsStringAsync(ct));
        Assert.Contains("The engine is offline, so nothing was saved.", html);
        Assert.Contains("port: 8080 <edited></textarea>", html);
        Assert.Contains("data-code-editor=\"yaml\"", html);

        var outside = await browser.PostAsync("/endpoints/1/volumes/data/files?handler=Save", PortFactory.Form(csrf, new() { ["path"] = "/../x", ["content"] = "" }), ct);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [Fact]
    public async Task Downloads_need_an_engine()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync("/endpoints/1/volumes/data/files?handler=Download&path=/hello.txt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
