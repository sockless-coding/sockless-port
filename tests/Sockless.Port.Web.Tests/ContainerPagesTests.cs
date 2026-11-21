using System.Net;

namespace Sockless.Port.Web.Tests;

/// <summary>Container pages with no engine: they must explain, not fail.</summary>
public sealed class ContainerPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/endpoints/1/containers")]
    [InlineData("/endpoints/1/containers/create")]
    [InlineData("/endpoints/1/containers/web")]
    [InlineData("/endpoints/1/containers/web/logs")]
    [InlineData("/endpoints/1/containers/web/stats")]
    [InlineData("/endpoints/1/containers/web/console")]
    [InlineData("/endpoints/1/containers/web/inspect")]
    public async Task Pages_render_the_offline_explanation(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/endpoints/1/containers/-bad")]
    [InlineData("/endpoints/1/containers/a%20b/logs")]
    [InlineData("/endpoints/9/containers")]
    public async Task Invalid_container_or_endpoint_is_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Containers_appear_in_the_navigation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/containers", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/endpoints/1/containers\"", html);
        Assert.Matches("aria-current=\"page\"[^>]*>\\s*<svg[^>]*>.*?</svg>\\s*<span>Containers</span>", System.Text.RegularExpressions.Regex.Replace(html, "\\s+", " ").Replace("> <", "><"));
    }

    [Fact]
    public async Task Bulk_action_while_offline_says_nothing_changed()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1/containers");

        var post = await browser.PostAsync("/endpoints/1/containers?handler=Bulk", PortFactory.Form(csrf, new() { ["action"] = "stop", ["ids"] = "abc" }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var html = await browser.GetStringAsync(post.Headers.Location!.OriginalString, TestContext.Current.CancellationToken);
        Assert.Contains("The engine is offline, so nothing was changed.", html);
    }

    [Fact]
    public async Task Pages_carry_a_style_nonce_matching_the_policy()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/endpoints/1/containers", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = System.Text.RegularExpressions.Regex.Match(policy, "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"name=\"csp-style-nonce\" content=\"{nonce}\"", html);
        Assert.DoesNotContain("unsafe-inline", policy);
    }
}
