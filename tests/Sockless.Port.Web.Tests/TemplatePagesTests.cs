using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Tests;

/// <summary>Templates with no engine: the built-in list, and stacks made from it (saved, not deployed).</summary>
public sealed class TemplatePagesTests : IDisposable
{
    private const string Password = "a long enough password";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_built_in_templates_are_listed_searched_and_paged()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var list = await browser.GetStringAsync("/endpoints/1/templates", ct);
        Assert.Contains("Uptime Kuma", list);
        Assert.Contains("href=\"/endpoints/1/templates/0/uptime-kuma\"", list);
        Assert.Contains("href=\"/template-sources\"", list);
        // Built-in templates have no logos, so the page fetches nothing.
        Assert.DoesNotContain("/logo\"", list);

        var search = await browser.GetStringAsync("/endpoints/1/templates?q=postgres", ct);
        Assert.Contains("PostgreSQL", search);
        Assert.DoesNotContain("Uptime Kuma", search);

        var category = await browser.GetStringAsync("/endpoints/1/templates?category=Database", ct);
        Assert.Contains("MariaDB", category);
        Assert.DoesNotContain("Jellyfin", category);

        var none = await browser.GetStringAsync("/endpoints/1/templates?q=nothing-like-this", ct);
        Assert.Contains("No templates match", none);

        var stacks = await browser.GetStringAsync("/endpoints/1/stacks", ct);
        Assert.Contains("href=\"/endpoints/1/templates\"", stacks);
    }

    [Fact]
    public async Task A_template_shows_its_files_and_risks_and_saves_a_stack_to_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var page = await browser.GetStringAsync("/endpoints/1/templates/0/postgresql", ct);
        Assert.Contains("name=\"Form.Variables[POSTGRES_PASSWORD]\"", page);
        Assert.Contains("name=\"Form.Ports[0].Host\"", page);
        Assert.Contains("image: postgres:17", page);
        Assert.DoesNotContain("reaches into the host", page);

        var dozzle = await browser.GetStringAsync("/endpoints/1/templates/0/dozzle", ct);
        Assert.Contains("reaches into the host", dozzle);
        Assert.Contains("/var/run/docker.sock", dozzle);

        // Bad choices are shown on their fields, and nothing is saved.
        var bad = await PostAsync(browser, "/endpoints/1/templates/0/postgresql", "/endpoints/1/templates/0/postgresql", new()
        {
            ["Form.Name"] = "db",
            ["Form.Variables[POSTGRES_PASSWORD]"] = "it's $5",
            ["Form.Ports[0].Publish"] = "true",
            ["Form.Ports[0].Host"] = "99999",
            ["Form.Volumes[0].Include"] = "true",
            ["Form.Volumes[0].Source"] = "pgdata",
        });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badHtml = await bad.Content.ReadAsStringAsync(ct);
        Assert.Contains("Use either &#x27; or $ in this value", badHtml);
        Assert.Contains("Enter a port", badHtml);
        Assert.Null(await _factory.Services.GetRequiredService<StackStore>().GetAsync(1, "db", ct));

        var saved = await PostAsync(browser, "/endpoints/1/templates/0/postgresql", "/endpoints/1/templates/0/postgresql", new()
        {
            ["Form.Name"] = "db",
            ["Form.Variables[POSTGRES_PASSWORD]"] = "s3cret",
            ["Form.Variables[POSTGRES_USER]"] = "app",
            ["Form.Ports[0].Publish"] = "false",
            ["Form.Volumes[0].Include"] = "true",
            ["Form.Volumes[0].Source"] = "pgdata",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/endpoints/1/stacks/db/editor?review=true", saved.Headers.Location!.OriginalString);

        var stack = await _factory.Services.GetRequiredService<StackStore>().GetAsync(1, "db", ct);
        Assert.Equal("PostgreSQL (Built-in)", stack!.Template);
        Assert.Null(stack.DeployedAt);
        var folder = Path.Combine(_factory.Services.GetRequiredService<IOptions<PortOptions>>().Value.StacksPath, "1", "db");
        var compose = File.ReadAllText(Path.Combine(folder, "compose.yaml"));
        Assert.Contains("pgdata:/var/lib/postgresql/data", compose);
        Assert.DoesNotContain("ports:", compose);
        Assert.Contains("POSTGRES_PASSWORD='s3cret'", File.ReadAllText(Path.Combine(folder, ".env")));

        var detail = await browser.GetStringAsync("/endpoints/1/stacks/db", ct);
        Assert.Contains("Made from the template PostgreSQL (Built-in)", detail);

        // The name is taken now; the form offers the next one.
        Assert.Contains("value=\"postgres\"", await browser.GetStringAsync("/endpoints/1/templates/0/postgresql", ct));
        var again = await PostAsync(browser, "/endpoints/1/templates/0/postgresql", "/endpoints/1/templates/0/postgresql", new() { ["Form.Name"] = "db" });
        Assert.Contains("There&#x27;s already a stack named db.", await again.Content.ReadAsStringAsync(ct));

        var audit = _factory.Services.GetRequiredService<AuditLog>();
        var entries = await audit.QueryAsync(new AuditQuery(), ct);
        Assert.Contains(entries.Entries, e => e.Action == "Templates: create stack" && e.Target == "db (PostgreSQL, Built-in)" && e.Outcome == AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task Unknown_templates_are_not_found_and_viewers_cannot_use_templates()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/endpoints/1/templates/0/no-such-app", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/endpoints/1/templates/7/postgresql", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/templates/0/postgresql/logo", ct)).StatusCode);

        await PortFactory.CreateUserAsync(admin, "vic", Password, EndpointRole.Viewer);
        var viewer = await _factory.SignInAsync("vic", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/endpoints/1/templates", ct)).StatusCode);
        Assert.DoesNotContain("href=\"/endpoints/1/templates\"", await viewer.GetStringAsync("/endpoints/1", ct));
        Assert.NotEqual(HttpStatusCode.OK, (await viewer.GetAsync("/template-sources", ct)).StatusCode);

        await PortFactory.CreateUserAsync(admin, "opal", Password, EndpointRole.Operator);
        var operatorBrowser = await _factory.SignInAsync("opal", Password);
        Assert.Contains("href=\"/endpoints/1/templates\"", await operatorBrowser.GetStringAsync("/endpoints/1", ct));
        var list = await operatorBrowser.GetStringAsync("/endpoints/1/templates", ct);
        Assert.Contains("Uptime Kuma", list);
        Assert.NotEqual(HttpStatusCode.OK, (await operatorBrowser.GetAsync("/template-sources", ct)).StatusCode);
    }

    [Fact]
    public async Task The_sources_page_lists_the_built_in_list_and_suggestions_and_refuses_bad_feeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();

        var page = await admin.GetStringAsync("/template-sources", ct);
        Assert.Contains("Built-in", page);
        Assert.Contains("Comes with Sockless Port", page);
        Assert.Contains("https://api.linuxserver.io/api/v1/images?include_config=true", page);

        var refused = await PostAsync(admin, "/template-sources", "/template-sources", new() { ["Input.Name"] = "Mine", ["Input.Url"] = "file:///etc/passwd" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Enter the feed&#x27;s http:// or https:// address.", await refused.Content.ReadAsStringAsync(ct));

        var unknown = await PostAsync(admin, "/template-sources", "/template-sources?handler=Suggested", new() { ["url"] = "https://example.com/not-suggested.json" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
