using System.Net;
using System.Text.RegularExpressions;

namespace Sockless.Port.Web.Tests;

/// <summary>Image and registry pages with no engine: they must explain, not fail.</summary>
public sealed partial class ImagePagesTests : IDisposable
{
    private const string Id = "a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/endpoints/1/images")]
    [InlineData("/endpoints/1/images/build")]
    [InlineData("/endpoints/1/images/" + Id)]
    [InlineData("/endpoints/1/images/" + Id + "/history")]
    [InlineData("/endpoints/1/images/a7dbc319b143/inspect")]
    public async Task Pages_render_the_offline_explanation(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/endpoints/1/images/nginx")]
    [InlineData("/endpoints/1/images/sha256:" + Id)]
    [InlineData("/endpoints/1/images/abc")]
    [InlineData("/endpoints/9/images")]
    [InlineData("/registries/42")]
    public async Task Invalid_image_endpoint_or_registry_is_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Images_and_registries_appear_in_the_navigation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/images", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/endpoints/1/images\"", html);
        Assert.Contains("href=\"/registries\"", html);
        Assert.Matches("aria-current=\"page\"[^>]*>\\s*<svg[^>]*>.*?</svg>\\s*<span>Images</span>", Regex.Replace(html, "\\s+", " ").Replace("> <", "><"));
    }

    [Fact]
    public async Task Actions_while_offline_say_nothing_changed()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/registries");
        var ct = TestContext.Current.CancellationToken;

        foreach (var (handler, fields) in new (string, Dictionary<string, string>)[]
                 {
                     ("Pull", new() { ["reference"] = "busybox:1.37" }),
                     ("Prune", new() { ["all"] = "true" }),
                     ("CheckUpdates", new()),
                 })
        {
            var post = await browser.PostAsync($"/endpoints/1/images?handler={handler}", PortFactory.Form(csrf, fields), ct);
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("The engine is offline, so nothing was changed.", await browser.GetStringAsync(post.Headers.Location!.OriginalString, ct));
        }
    }

    [Fact]
    public async Task A_pull_of_something_that_isnt_an_image_is_refused()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/registries");

        var post = await browser.PostAsync("/endpoints/1/images?handler=Pull", PortFactory.Form(csrf, new() { ["reference"] = "two words" }), TestContext.Current.CancellationToken);

        Assert.Contains("Enter an image such as nginx", await browser.GetStringAsync(post.Headers.Location!.OriginalString, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- registries

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient browser, string page, Dictionary<string, string> form, string? action = null)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, page);
        return await browser.PostAsync(action ?? page, PortFactory.Form(csrf, form), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Registry_credentials_are_added_edited_and_removed_without_showing_the_password()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var ct = TestContext.Current.CancellationToken;

        var add = await SaveAsync(browser, "/registries", new()
        {
            ["Form.Name"] = "Company GHCR",
            ["Form.Registry"] = "https://GHCR.io/",
            ["Form.Username"] = "deploy-bot",
            ["Form.Password"] = "ghp_TopSecretToken",
        });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        var list = await browser.GetStringAsync("/registries", ct);
        Assert.Contains("Saved the credentials for ghcr.io.", list);
        Assert.Contains("Company GHCR", list);
        Assert.DoesNotContain("ghp_TopSecretToken", list);

        var editUrl = EditLink().Match(list).Groups[1].Value;
        var edit = await browser.GetStringAsync(editUrl, ct);
        Assert.Contains("value=\"deploy-bot\"", edit);
        Assert.DoesNotContain("ghp_TopSecretToken", edit);
        Assert.Contains("Leave empty to keep the stored one.", edit);

        // An edit without a password keeps the stored one (the form is valid without it).
        var save = await SaveAsync(browser, editUrl, new() { ["Form.Name"] = "GHCR", ["Form.Registry"] = "ghcr.io", ["Form.Username"] = "deploy-bot-2", ["Form.Password"] = "" });
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Saved GHCR.", await browser.GetStringAsync(editUrl, ct));

        // Testing needs an engine; there is none.
        var test = await SaveAsync(browser, editUrl, [], $"{editUrl}?handler=Test");
        Assert.Contains("No engine is online to test the login with.", await browser.GetStringAsync(test.Headers.Location!.OriginalString, ct));

        var remove = await SaveAsync(browser, editUrl, [], $"{editUrl}?handler=Remove");
        Assert.Equal("/registries", remove.Headers.Location!.OriginalString);
        var after = await browser.GetStringAsync("/registries", ct);
        Assert.Contains("Removed the credentials GHCR.", after);
        Assert.Contains("No registry credentials yet", after);
    }

    [Fact]
    public async Task Registry_form_errors_are_shown_on_the_fields()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await SaveAsync(browser, "/registries", new() { ["Form.Name"] = "x", ["Form.Registry"] = "not a host", ["Form.Username"] = "u", ["Form.Password"] = "p" });
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Use a registry host such as docker.io", html);
        // Fields bind back to the page's Form property.
        Assert.Contains("name=\"Form.Registry\"", html);
        Assert.Matches("id=\"Form_Registry\"[^>]*aria-invalid=\"true\"|aria-invalid=\"true\"[^>]*id=\"Form_Registry\"", html);
        // The password is never echoed back.
        Assert.DoesNotContain("value=\"p\"", html);
    }

    [GeneratedRegex("href=\"(/registries/\\d+)\"")]
    private static partial Regex EditLink();
}
