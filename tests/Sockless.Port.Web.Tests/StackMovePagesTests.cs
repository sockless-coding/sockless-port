using System.Net;
using System.Text.RegularExpressions;
using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>The move page, against a local engine where nothing listens and an agent that never enrolled.</summary>
public sealed partial class StackMovePagesTests : IDisposable
{
    private const string Password = "another long password";
    private const string ComposeFile = "services:\n  web:\n    image: nginx:1.27\n";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A saved stack "shop" on the local endpoint, and an agent "nas" to move it to.</summary>
    private async Task<(HttpClient Browser, int Nas)> ArrangeAsync()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1/stacks/create");
        var created = await browser.PostAsync("/endpoints/1/stacks/create", PortFactory.Form(token, new()
        {
            ["Input.Name"] = "shop",
            ["Input.Compose"] = ComposeFile,
            ["deploy"] = "false",
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add");
        var added = await browser.PostAsync("/endpoints/add", PortFactory.Form(token, new() { ["Name"] = "nas", ["Address"] = "127.0.0.1:1" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        return (browser, int.Parse(SettingsPath().Match(added.Headers.Location!.OriginalString).Groups[1].Value));
    }

    [Fact]
    public async Task The_page_offers_the_other_endpoints_and_shows_what_blocks_the_move()
    {
        var (browser, nas) = await ArrangeAsync();

        var choose = await browser.GetStringAsync("/endpoints/1/stacks/shop/move", Ct);
        Assert.Contains(">Move shop</h2>", choose);
        Assert.Contains($"<option value=\"{nas}\">nas (offline)</option>", choose);
        Assert.DoesNotContain("<option value=\"1\"", choose);            // not to itself
        Assert.DoesNotContain("What the checks found", choose);

        var checkedPage = await browser.GetStringAsync($"/endpoints/1/stacks/shop/move?to={nas}", Ct);
        Assert.Contains("What the checks found", checkedPage);
        Assert.Contains("Blocks the move", checkedPage);
        Assert.Contains("local is offline", checkedPage);
        Assert.DoesNotContain("Move shop</button>", checkedPage);     // no form while something blocks it
    }

    [Fact]
    public async Task A_blocked_move_is_refused_with_the_reason_and_audited()
    {
        var (browser, nas) = await ArrangeAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, $"/endpoints/1/stacks/shop/move?to={nas}");

        var post = await browser.PostAsync("/endpoints/1/stacks/shop/move", PortFactory.Form(token, new()
        {
            ["to"] = nas.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["copyVolumes"] = "true",
            ["after"] = "Remove",
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal($"/endpoints/1/stacks/shop/move?to={nas}", post.Headers.Location!.OriginalString);
        Assert.Contains("local is offline", await browser.GetStringAsync(post.Headers.Location.OriginalString, Ct));
        var audit = await browser.GetStringAsync("/audit?outcome=Failed", Ct);
        Assert.Contains("Stacks: move", audit);
        Assert.Contains("shop to nas", audit);
    }

    [Fact]
    public async Task A_stack_that_isnt_managed_has_nothing_to_move()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/endpoints/1/stacks/nothing/move", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_endpoints_the_user_may_deploy_on_are_offered()
    {
        var (admin, nas) = await ArrangeAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None,
            new Dictionary<int, EndpointRole> { [1] = EndpointRole.Operator, [nas] = EndpointRole.Viewer });
        var user = await _factory.SignInAsync("ann", Password);

        var html = await user.GetStringAsync("/endpoints/1/stacks/shop/move", Ct);

        Assert.Contains("No other endpoint to move it to", html);

        var token = await PortFactory.GetAntiforgeryTokenAsync(user, "/endpoints/1/stacks/shop/move");
        var post = await user.PostAsync("/endpoints/1/stacks/shop/move", PortFactory.Form(token, new()
        {
            ["to"] = nas.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["after"] = "Remove",
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("Choose an endpoint you may deploy this stack on.", await user.GetStringAsync(post.Headers.Location!.OriginalString, Ct));
    }

    [Fact]
    public async Task A_viewer_cant_open_the_page()
    {
        var (admin, _) = await ArrangeAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.Viewer);
        var viewer = await _factory.SignInAsync("ann", Password);

        var response = await viewer.GetAsync("/endpoints/1/stacks/shop/move", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [GeneratedRegex(@"/endpoints/(\d+)/settings")]
    private static partial Regex SettingsPath();
}
