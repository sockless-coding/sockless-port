using System.Net;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>Endpoint pages against an engine address where nothing listens.</summary>
public sealed class EndpointPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Local_endpoint_is_registered_from_configuration()
    {
        await _factory.CreateSignedInBrowserAsync();

        Assert.Equal("local", _factory.LocalEndpoint.Name);
        Assert.Equal(PortFactory.UnreachableEngine, _factory.LocalEndpoint.Address.ToString());
    }

    [Fact]
    public async Task Home_redirects_to_the_current_endpoint()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/endpoints/{_factory.LocalEndpoint.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unreachable_engine_shows_why_it_is_offline()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var html = await browser.GetStringAsync($"/endpoints/{_factory.LocalEndpoint.Id}", TestContext.Current.CancellationToken);

        Assert.Contains("The engine is offline", html);
        Assert.Contains("/nonexistent/sockless-port-test.sock", html);
        Assert.Contains("Engine offline", html);       // top bar live indicator, a word not just a colour
        Assert.Contains("data-endpoint-id=\"1\"", html);
    }

    [Fact]
    public async Task Unknown_endpoint_is_not_found()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/endpoints/999", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_page_lists_the_local_endpoint()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var html = await browser.GetStringAsync("/endpoints", TestContext.Current.CancellationToken);

        Assert.Contains(PortFactory.UnreachableEngine, html);
        Assert.Contains(">Offline<", html);
        Assert.Contains(">Local<", html);
    }

    [Theory]
    [InlineData("/endpoints/1?handler=Overview", "engine-overview")]
    [InlineData("/endpoints/1?handler=DiskUsage", "disk-usage")]
    [InlineData("/endpoints?handler=Table", "endpoint-table")]
    [InlineData("/endpoints/topbar?current=1", "topbar-status")]
    public async Task Partials_render_without_the_layout(string path, string elementId)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains($"id=\"{elementId}\"", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Expired_session_on_an_htmx_request_redirects_the_whole_page()
    {
        await _factory.CreateSignedInBrowserAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints/1?handler=Overview");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Current-URL", "http://localhost/endpoints/1");

        var response = await _factory.CreateBrowser().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("/account/login?returnUrl=%2Fendpoints%2F1", response.Headers.GetValues("HX-Redirect").Single());
    }

    [Fact]
    public async Task Hub_requires_sign_in()
    {
        await _factory.CreateSignedInBrowserAsync();

        var response = await _factory.CreateBrowser().PostAsync("/hubs/engine/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_negotiates_for_signed_in_users()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.PostAsync("/hubs/engine/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
