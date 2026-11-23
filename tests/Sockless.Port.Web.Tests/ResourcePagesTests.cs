using System.Net;
using Sockless.Port.Access;

namespace Sockless.Port.Web.Tests;

/// <summary>The endpoint's Resources page, with no engine.</summary>
public sealed class ResourcePagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Page_renders_the_offline_explanation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", html);
        Assert.DoesNotContain("data-resources ", html); // no live table without an engine
    }

    [Fact]
    public async Task Resources_appear_in_the_navigation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);

        Assert.Matches("aria-current=\"page\"[^>]*>\\s*<svg[^>]*>.*?</svg>\\s*<span>Resources</span>", System.Text.RegularExpressions.Regex.Replace(html, "\\s+", " ").Replace("> <", "><"));
    }

    [Fact]
    public async Task Unknown_endpoint_is_not_found()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/endpoints/9/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_open_it_and_a_user_without_access_cannot()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "vera", "Correct-Horse-9", EndpointRole.Viewer);
        await PortFactory.CreateUserAsync(admin, "noel", "Correct-Horse-9", EndpointRole.None);
        var viewer = await _factory.SignInAsync("vera", "Correct-Horse-9");
        var outsider = await _factory.SignInAsync("noel", "Correct-Horse-9");

        var seen = await viewer.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var hidden = await outsider.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
