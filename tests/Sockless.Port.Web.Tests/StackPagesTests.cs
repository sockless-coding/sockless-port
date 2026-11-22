using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Tests;

/// <summary>Stack pages with no engine: stacks can still be written and saved, but not deployed.</summary>
public sealed partial class StackPagesTests : IDisposable
{
    private const string ComposeFile = "services:\n  web:\n    image: nginx:${TAG}\n";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<bool> ComposeInstalledAsync() =>
        (await _factory.Services.GetRequiredService<ComposeCli>().StatusAsync(TestContext.Current.CancellationToken)).Tool is not null;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_list_and_the_create_form_render_while_offline()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var list = await browser.GetStringAsync("/endpoints/1/stacks", TestContext.Current.CancellationToken);
        Assert.Contains("The engine is offline", list);
        Assert.Contains("No stacks yet", list);
        Assert.Contains("href=\"/endpoints/1/stacks/create\"", list);

        var create = await browser.GetStringAsync("/endpoints/1/stacks/create", TestContext.Current.CancellationToken);
        Assert.Contains("data-code-editor=\"yaml\"", create);
        Assert.Contains("name=\"Input.Env\"", create);
    }

    [Fact]
    public async Task A_stack_is_saved_edited_and_reviewed_but_not_deployed_while_offline()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var created = await PostAsync(browser, "/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = "shop",
            ["Input.Compose"] = ComposeFile,
            ["Input.Env"] = "TAG=1.26",
            ["deploy"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/endpoints/1/stacks/shop", created.Headers.Location!.OriginalString);
        var detail = await browser.GetStringAsync("/endpoints/1/stacks/shop", ct);
        Assert.Contains("Saved shop. It isn&#x27;t deployed yet.", detail);
        Assert.Contains("Saved but not deployed", detail);

        var files = Path.Combine(_factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortOptions>>().Value.StacksPath, "1", "shop");
        Assert.Equal(ComposeFile, File.ReadAllText(Path.Combine(files, "compose.yaml")));
        Assert.Equal("TAG=1.26\n", File.ReadAllText(Path.Combine(files, ".env")));

        var list = await browser.GetStringAsync("/endpoints/1/stacks", ct);
        Assert.Contains(">shop</a>", list);
        Assert.Contains("Never", list);

        // A second stack with the same name is refused on the field.
        var again = await PostAsync(browser, "/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = "shop", ["Input.Compose"] = ComposeFile, ["deploy"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("There&#x27;s already a stack named shop.", await again.Content.ReadAsStringAsync(ct));

        var editor = await browser.GetStringAsync("/endpoints/1/stacks/shop/editor", ct);
        Assert.Contains("TAG=1.26", editor);

        var review = await PostAsync(browser, "/endpoints/1/stacks/shop/editor", "/endpoints/1/stacks/shop/editor?handler=Review", new()
        {
            ["Input.Compose"] = ComposeFile, ["Input.Env"] = "TAG=1.27",
        });
        var reviewHtml = await review.Content.ReadAsStringAsync(ct);
        Assert.Contains("Nothing was deployed from here yet", reviewHtml);
        var reviewed = ReviewedField().Match(reviewHtml).Groups[1].Value;
        Assert.Matches("^[0-9a-f]{64}$", reviewed);

        // Changed after the review: shown again instead of deployed.
        var stale = await PostAsync(browser, "/endpoints/1/stacks/shop/editor", "/endpoints/1/stacks/shop/editor?handler=Deploy", new()
        {
            ["Input.Compose"] = ComposeFile, ["Input.Env"] = "TAG=1.28", ["Reviewed"] = reviewed,
        });
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("The files changed after the review.", await stale.Content.ReadAsStringAsync(ct));

        // As reviewed: saved, but the offline engine can't deploy it.
        var deploy = await PostAsync(browser, "/endpoints/1/stacks/shop/editor", "/endpoints/1/stacks/shop/editor?handler=Deploy", new()
        {
            ["Input.Compose"] = ComposeFile, ["Input.Env"] = "TAG=1.27", ["Reviewed"] = reviewed,
        });
        Assert.Equal(HttpStatusCode.Redirect, deploy.StatusCode);
        var after = await browser.GetStringAsync(deploy.Headers.Location!.OriginalString, ct);
        Assert.Contains("Saved shop, but it wasn&#x27;t deployed", after);
        Assert.Equal("TAG=1.27\n", File.ReadAllText(Path.Combine(files, ".env")));
    }

    [Fact]
    public async Task Names_and_files_are_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();

        var badName = await PostAsync(browser, "/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = "My App", ["Input.Compose"] = ComposeFile, ["deploy"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, badName.StatusCode);
        Assert.Contains("Use lower-case letters, digits, - and _", await badName.Content.ReadAsStringAsync(ct));

        Assert.SkipUnless(await ComposeInstalledAsync(), "Compose isn't installed here.");
        var broken = await PostAsync(browser, "/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = "broken", ["Input.Compose"] = "services:\n  web:\n    image: nginx\n   command: x\n", ["deploy"] = "false",
        });
        var html = await broken.Content.ReadAsStringAsync(ct);
        Assert.Contains("Compose rejected the files", html);
        Assert.Contains("Line 4:", html);

        // htmx validation returns the result alone.
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1/stacks/create");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/endpoints/1/stacks/create?handler=Validate")
        {
            Content = PortFactory.Form(csrf, new() { ["Input.Compose"] = ComposeFile, ["Input.Env"] = "TAG=1" }),
        };
        request.Headers.Add("HX-Request", "true");
        var partial = await (await browser.SendAsync(request, ct)).Content.ReadAsStringAsync(ct);
        Assert.StartsWith("<div id=\"stack-validation\"", partial.Trim());
        Assert.Contains("Valid. 1 service", partial);
    }

    [Theory]
    [InlineData("/endpoints/1/stacks/nothing-here")]
    [InlineData("/endpoints/1/stacks/Bad.Name")]
    [InlineData("/endpoints/1/stacks/nothing-here/editor")]
    [InlineData("/endpoints/9/stacks")]
    public async Task Unknown_stacks_and_endpoints_are_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Stacks_appear_in_the_navigation_with_their_scripts()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/stacks", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/endpoints/1/stacks\"", html);
        Assert.Matches("""<script src="/js/stacks(\.[a-z0-9]+)?\.js" """.TrimEnd(), html);
        Assert.Matches("""<script src="/lib/codemirror/codemirror(\.[a-z0-9]+)?\.js" """.TrimEnd(), html);
    }

    [GeneratedRegex("name=\"Reviewed\" value=\"([0-9a-f]+)\"")]
    private static partial Regex ReviewedField();
}
