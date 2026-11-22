using System.Net;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

/// <summary>Swarm pages with no engine: they must explain, not fail, and stay out of the navigation.</summary>
public sealed class SwarmPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/endpoints/1/swarm")]
    [InlineData("/endpoints/1/swarm/services")]
    [InlineData("/endpoints/1/swarm/services/create")]
    [InlineData("/endpoints/1/swarm/services/web")]
    [InlineData("/endpoints/1/swarm/services/web/edit")]
    [InlineData("/endpoints/1/swarm/services/web/logs")]
    [InlineData("/endpoints/1/swarm/services/web/inspect")]
    [InlineData("/endpoints/1/swarm/nodes")]
    [InlineData("/endpoints/1/swarm/nodes/abc123")]
    [InlineData("/endpoints/1/swarm/nodes/abc123/inspect")]
    [InlineData("/endpoints/1/swarm/secrets")]
    [InlineData("/endpoints/1/swarm/secrets/create")]
    [InlineData("/endpoints/1/swarm/secrets/db_password")]
    [InlineData("/endpoints/1/swarm/configs")]
    [InlineData("/endpoints/1/swarm/configs/create")]
    [InlineData("/endpoints/1/swarm/configs/app_conf")]
    public async Task Pages_render_the_offline_explanation(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/endpoints/1/swarm/services/-web")]
    [InlineData("/endpoints/1/swarm/services/a%20b/logs")]
    [InlineData("/endpoints/1/swarm/nodes/.x")]
    [InlineData("/endpoints/1/swarm/secrets/_x")]
    [InlineData("/endpoints/9/swarm")]
    [InlineData("/endpoints/9/swarm/services")]
    public async Task Invalid_names_and_endpoints_are_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Swarm_navigation_is_hidden_without_a_swarm()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var html = await browser.GetStringAsync("/endpoints/1", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/endpoints/1/networks\"", html);
        Assert.DoesNotContain("href=\"/endpoints/1/swarm", html);
        Assert.DoesNotContain("id=\"nav-swarm\"", html);
    }

    [Fact]
    public async Task Writes_while_offline_change_nothing()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/registries");
        var ct = TestContext.Current.CancellationToken;

        foreach (var action in (string[])["/endpoints/1/swarm/services?handler=Remove&id=web", "/endpoints/1/swarm?handler=Rotate&role=worker"])
        {
            var post = await browser.PostAsync(action, PortFactory.Form(csrf, []), ct);
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            var page = WebUtility.HtmlDecode(await browser.GetStringAsync(post.Headers.Location!.OriginalString, ct));
            Assert.True(page.Contains("can only be", StringComparison.Ordinal) || page.Contains("nothing was changed", StringComparison.Ordinal), page[..Math.Min(400, page.Length)]);
        }
    }

    [Fact]
    public void Swarm_entries_need_the_capability()
    {
        var manager = new EngineDescriptor(EngineKind.Docker, "29.0", new Version(1, 51), "linux", "amd64", EngineCapabilities.Swarm | EngineCapabilities.SwarmManager);
        var worker = manager with { Capabilities = EngineCapabilities.Swarm };
        var plain = manager with { Capabilities = EngineCapabilities.None };

        Assert.All(Navigation.Swarm, item => Assert.True(item.IsAvailable(manager)));
        Assert.Equal(["Cluster"], Navigation.Swarm.Where(i => i.IsAvailable(worker)).Select(i => i.Label));
        Assert.DoesNotContain(Navigation.Swarm, i => i.IsAvailable(plain));
        Assert.DoesNotContain(Navigation.Swarm, i => i.IsAvailable(null));
        Assert.All(Navigation.Main, item => Assert.True(item.IsAvailable(plain)));
    }
}
