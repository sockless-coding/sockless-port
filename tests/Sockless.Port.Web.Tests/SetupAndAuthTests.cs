using System.Net;

namespace Sockless.Port.Web.Tests;

public sealed class SetupAndAuthTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_check_is_anonymous_and_healthy()
    {
        var response = await _factory.CreateBrowser().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Before_setup_pages_redirect_to_setup()
    {
        var response = await _factory.CreateBrowser().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/css/workshop.css")]
    [InlineData("/css/app.css")]
    [InlineData("/lib/htmx/htmx.min.js")]
    [InlineData("/favicon.svg")]
    public async Task Before_setup_static_assets_are_served(string path)
    {
        var response = await _factory.CreateBrowser().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_a_wrong_token()
    {
        var browser = _factory.CreateBrowser();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/setup");

        var response = await browser.PostAsync("/setup", PortFactory.Form(csrf, new()
        {
            ["Input.SetupToken"] = "not-the-token",
            ["Input.UserName"] = "admin",
            ["Input.Password"] = PortFactory.AdminPassword,
            ["Input.ConfirmPassword"] = PortFactory.AdminPassword,
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("setup token is not valid", html);
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.NotNull(_factory.SetupToken);
    }

    [Fact]
    public async Task Setup_creates_the_administrator_and_signs_in()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var dashboard = await browser.GetAsync("/endpoints/1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var html = await dashboard.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("aria-current=\"page\"", html);
        Assert.Contains(PortFactory.AdminUser, html);
        Assert.Null(_factory.SetupToken);
    }

    [Fact]
    public async Task After_setup_the_setup_page_is_closed()
    {
        await _factory.CreateSignedInBrowserAsync();

        var response = await _factory.CreateBrowser().GetAsync("/setup", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location?.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task After_setup_anonymous_requests_go_to_login()
    {
        await _factory.CreateSignedInBrowserAsync();

        var response = await _factory.CreateBrowser().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_with_wrong_password_shows_a_generic_error()
    {
        await _factory.CreateSignedInBrowserAsync();
        var browser = _factory.CreateBrowser();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");

        var response = await browser.PostAsync("/account/login", PortFactory.Form(csrf, new()
        {
            ["Input.UserName"] = PortFactory.AdminUser,
            ["Input.Password"] = "wrong password",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("user name or password is incorrect", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_and_logout_round_trip()
    {
        await _factory.CreateSignedInBrowserAsync();
        var browser = _factory.CreateBrowser();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");

        var login = await browser.PostAsync("/account/login?returnUrl=%2F", PortFactory.Form(csrf, new()
        {
            ["Input.UserName"] = PortFactory.AdminUser,
            ["Input.Password"] = PortFactory.AdminPassword,
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/endpoints/1", TestContext.Current.CancellationToken)).StatusCode);

        var logoutToken = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1");
        var logout = await browser.PostAsync("/account/logout", PortFactory.Form(logoutToken, []), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Login_ignores_external_return_urls()
    {
        await _factory.CreateSignedInBrowserAsync();
        var browser = _factory.CreateBrowser();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");

        var login = await browser.PostAsync("/account/login?returnUrl=https%3A%2F%2Fevil.example%2F", PortFactory.Form(csrf, new()
        {
            ["Input.UserName"] = PortFactory.AdminUser,
            ["Input.Password"] = PortFactory.AdminPassword,
        }), TestContext.Current.CancellationToken);

        Assert.Equal("/", login.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Logout_by_get_does_not_sign_out()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        await browser.GetAsync("/account/logout", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/endpoints/1", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_are_rejected()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.PostAsync("/account/logout", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _factory.CreateBrowser().GetAsync("/setup", TestContext.Current.CancellationToken);

        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}
