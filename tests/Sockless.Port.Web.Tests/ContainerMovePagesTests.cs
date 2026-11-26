using System.Net;
using System.Text.RegularExpressions;
using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>The container move page, against a local engine where nothing listens and an agent that never enrolled.</summary>
public sealed partial class ContainerMovePagesTests : IDisposable
{
    private const string Password = "another long password";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Browser, int Nas)> ArrangeAsync()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add");
        var added = await browser.PostAsync("/endpoints/add", PortFactory.Form(token, new() { ["Name"] = "nas", ["Address"] = "127.0.0.1:1" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        return (browser, int.Parse(SettingsPath().Match(added.Headers.Location!.OriginalString).Groups[1].Value));
    }

    [Fact]
    public async Task The_page_says_the_endpoint_is_offline()
    {
        var (browser, _) = await ArrangeAsync();

        var html = await browser.GetStringAsync("/endpoints/1/containers/web/move", Ct);

        Assert.Contains(">Move web</h2>", html);
        Assert.Contains("The engine is offline", html);
        Assert.DoesNotContain("Where to", html);
    }

    [Fact]
    public async Task A_move_from_an_offline_endpoint_is_refused_with_the_reason_and_audited()
    {
        var (browser, nas) = await ArrangeAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1/containers/web/move");

        var post = await browser.PostAsync("/endpoints/1/containers/web/move", PortFactory.Form(token, new()
        {
            ["to"] = nas.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["copyVolumes"] = "true",
            ["after"] = "Remove",
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal($"/endpoints/1/containers/web/move?to={nas}", post.Headers.Location!.OriginalString);
        Assert.Contains("local is offline", await browser.GetStringAsync(post.Headers.Location.OriginalString, Ct));
        var audit = await browser.GetStringAsync("/audit?outcome=Failed", Ct);
        Assert.Contains("Containers: move", audit);
        Assert.Contains("web to nas", audit);
    }

    [Fact]
    public async Task A_viewer_cant_open_the_page()
    {
        var (admin, _) = await ArrangeAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.Viewer);
        var viewer = await _factory.SignInAsync("ann", Password);

        var response = await viewer.GetAsync("/endpoints/1/containers/web/move", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_target_the_user_cant_operate_is_refused()
    {
        var (admin, nas) = await ArrangeAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None,
            new Dictionary<int, EndpointRole> { [1] = EndpointRole.Operator, [nas] = EndpointRole.Viewer });
        var user = await _factory.SignInAsync("ann", Password);
        var token = await PortFactory.GetAntiforgeryTokenAsync(user, "/endpoints/1/containers/web/move");

        var post = await user.PostAsync("/endpoints/1/containers/web/move", PortFactory.Form(token, new()
        {
            ["to"] = nas.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["after"] = "Remove",
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("Choose an endpoint you may run containers on.", await user.GetStringAsync(post.Headers.Location!.OriginalString, Ct));
    }

    [GeneratedRegex(@"/endpoints/(\d+)/settings")]
    private static partial Regex SettingsPath();
}
