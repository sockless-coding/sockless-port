using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Tests;

/// <summary>Git-backed stack pages and the webhook, with no engine and no network: the stack is made through the store.</summary>
public sealed partial class GitStackPagesTests : IDisposable
{
    private const string Compose = "services:\n  web:\n    image: nginx:${TAG}\n";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string RepositoryUrl = "https://git.example.invalid/team/app.git";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    /// <summary>A git-backed stack "web" on endpoint 1, as if fetched at <see cref="Commit"/>.</summary>
    private async Task<StackDefinition> CreateStackAsync(string? envPath = null)
    {
        var store = _factory.Services.GetRequiredService<StackStore>();
        var staged = Path.Combine(_factory.Services.GetRequiredService<IOptions<PortOptions>>().Value.TempPath, $"staged-{Guid.NewGuid():n}");
        Directory.CreateDirectory(staged);
        var fetched = new GitFetchResult(new GitCommit(Commit, "Ada", DateTimeOffset.UtcNow.AddHours(-1), "Add the web service"), Compose,
            envPath is null ? null : "TAG=from-repo\n", null);
        return await store.CreateFromGitAsync(1, "web", StackKind.Compose,
            new GitSettings(RepositoryUrl, "main", "deploy/compose.yaml", envPath, GitAuth.Token, "bot", "t0ken-value", null, 60), fetched, staged, "TAG=1.27",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_create_form_offers_a_repository_and_checks_its_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var plain = await browser.GetStringAsync("/endpoints/1/stacks/create", ct);
        Assert.Contains("href=\"/endpoints/1/stacks/create?source=git\"", plain);

        var form = await browser.GetStringAsync("/endpoints/1/stacks/create?source=git", ct);
        Assert.Contains("name=\"Git.Url\"", form);
        Assert.Contains("name=\"Git.Auth\"", form);
        Assert.Contains("Fetch and deploy", form);
        Assert.DoesNotContain("data-code-editor=\"yaml\"", form);

        var refused = await PostAsync(browser, "/endpoints/1/stacks/create?source=git", "/endpoints/1/stacks/create?source=git", new()
        {
            ["Input.Name"] = "web", ["Git.Url"] = "file:///etc", ["Git.Branch"] = "main", ["Git.ComposePath"] = "../compose.yaml",
            ["Git.Auth"] = "Token", ["Git.PollMinutes"] = "0", ["deploy"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync(ct));
        Assert.Contains("Other transports (file://, git://, ext::) aren't allowed.", html);
        Assert.Contains("The path can't contain empty parts, . or ..", html);
        Assert.Contains("Enter the access token.", html);
        Assert.Null(await _factory.Services.GetRequiredService<StackStore>().GetAsync(1, "web", ct));
    }

    [Fact]
    public async Task A_deploy_key_is_generated_on_the_form_and_kept_out_of_the_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();

        var generated = await PostAsync(browser, "/endpoints/1/stacks/create?source=git", "/endpoints/1/stacks/create?handler=GenerateKey&source=git", new()
        {
            ["Input.Name"] = "web", ["Git.Url"] = "git@git.example.invalid:team/app.git", ["Git.Auth"] = "None",
        });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var html = WebUtility.HtmlDecode(await generated.Content.ReadAsStringAsync(ct));
        Assert.Matches("ecdsa-sha2-nistp256 AAAA[A-Za-z0-9+/=]+ sockless-port web@local", html);
        Assert.DoesNotContain("PRIVATE KEY", html);
        Assert.Matches("name=\"Git.GeneratedKey\" value=\"[^\"]{40,}\"", html);
        Assert.Matches("value=\"SshKey\"[^>]*checked", html);
    }

    [Fact]
    public async Task A_git_stack_shows_its_repository_and_its_files_are_read_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        await CreateStackAsync();

        var list = await browser.GetStringAsync("/endpoints/1/stacks", ct);
        Assert.Contains(">Git</span>", list);

        var detail = await browser.GetStringAsync("/endpoints/1/stacks/web", ct);
        Assert.Contains(RepositoryUrl, detail);
        Assert.Contains("Add the web service", detail);
        Assert.Contains(">0123456</a>", detail);
        Assert.Contains("Every hour", detail);
        Assert.Contains("value=\"fetch\"", detail);
        Assert.Contains("href=\"/endpoints/1/stacks/web/repository\"", detail);
        Assert.DoesNotContain("t0ken-value", detail);

        var editor = await browser.GetStringAsync("/endpoints/1/stacks/web/editor", ct);
        Assert.Matches("id=\"Input_Compose\"[^>]*readonly", editor);
        Assert.DoesNotMatch("id=\"Input_Env\"[^>]*readonly", editor);
        Assert.Contains("Save .env without deploying", editor);

        // The compose file can't be changed through the form; the local .env can.
        var saved = await PostAsync(browser, "/endpoints/1/stacks/web/editor", "/endpoints/1/stacks/web/editor?handler=Save", new()
        {
            ["Input.Compose"] = "services:\n  evil:\n    image: busybox\n", ["Input.Env"] = "TAG=1.28",
        });
        // Without Compose here the files can't be checked; with it they're saved. Either way the compose file stays the repository's.
        Assert.True(saved.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"Saving answered {saved.StatusCode}");
        var files = (await _factory.Services.GetRequiredService<StackStore>().Files.ReadAsync(1, "web", ct))!;
        Assert.Equal(Compose, files.Compose);
    }

    [Fact]
    public async Task A_repository_env_is_read_only_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        await CreateStackAsync(envPath: "deploy/.env");

        var editor = await browser.GetStringAsync("/endpoints/1/stacks/web/editor", ct);
        Assert.Matches("id=\"Input_Env\"[^>]*readonly", editor);
        Assert.Contains("TAG=from-repo", editor);
        Assert.DoesNotContain("Save .env without deploying", editor);
    }

    [Fact]
    public async Task The_repository_page_turns_the_webhook_on_shows_its_secret_and_disconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        var stack = await CreateStackAsync();
        var store = _factory.Services.GetRequiredService<StackStore>();

        var page = await browser.GetStringAsync("/endpoints/1/stacks/web/repository", ct);
        Assert.Contains($"value=\"{RepositoryUrl}\"", page);
        Assert.Contains("Saved; enter a new one to change it.", page);
        Assert.DoesNotContain("t0ken-value", page);
        Assert.Contains("Turn on the webhook", page);

        var on = await PostAsync(browser, "/endpoints/1/stacks/web/repository", "/endpoints/1/stacks/web/repository?handler=Webhook", []);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Contains("no-store", on.Headers.CacheControl?.ToString());
        var html = await on.Content.ReadAsStringAsync(ct);
        var repository = (await store.GetRepositoryAsync(stack.Id, ct))!;
        var secret = store.WebhookSecret(repository)!;
        Assert.Contains(secret, html);
        Assert.Contains($"/hooks/stacks/{repository.WebhookId}", html);

        // Later GETs offer to show it rather than showing it.
        var later = await browser.GetStringAsync("/endpoints/1/stacks/web/repository", ct);
        Assert.DoesNotContain(secret, later);
        Assert.Contains("Show the secret", later);
        var shown = await PostAsync(browser, "/endpoints/1/stacks/web/repository", "/endpoints/1/stacks/web/repository?handler=ShowWebhookSecret", []);
        Assert.Contains(secret, await shown.Content.ReadAsStringAsync(ct));

        var audit = await _factory.Services.GetRequiredService<AuditLog>().QueryAsync(new AuditQuery(), ct);
        Assert.Contains(audit.Entries, e => e.Action == "Stacks: turn on webhook" && e.Target == "web");
        Assert.Contains(audit.Entries, e => e.Action == "Stacks: show webhook secret");
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains(secret, StringComparison.Ordinal));

        var off = await PostAsync(browser, "/endpoints/1/stacks/web/repository", "/endpoints/1/stacks/web/repository?handler=Disconnect", []);
        Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        Assert.Null(await store.GetRepositoryAsync(stack.Id, ct));
        var editor = await browser.GetStringAsync("/endpoints/1/stacks/web/editor", ct);
        Assert.DoesNotMatch("id=\"Input_Compose\"[^>]*readonly", editor);
    }

    [Fact]
    public async Task The_webhook_checks_the_secret_and_queues_a_redeploy_for_pushes_to_the_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        var stack = await CreateStackAsync();
        var store = _factory.Services.GetRequiredService<StackStore>();
        var secret = (await store.SetWebhookAsync(stack.Id, enabled: true, ct))!;
        var hook = $"/hooks/stacks/{(await store.GetRepositoryAsync(stack.Id, ct))!.WebhookId}";
        // A host has no session and no antiforgery token.
        var host = _factory.CreateBrowser();

        async Task<HttpResponseMessage> CallAsync(string body, string? signWith, string githubEvent = "push")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, hook) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-GitHub-Event", githubEvent);
            if (signWith is not null)
                request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signWith), bytes)));
            return await host.SendAsync(request, ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync("/hooks/stacks/00000000000000000000000000000000", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync("/hooks/stacks/not-hex", null, ct)).StatusCode);

        var forged = await CallAsync("""{"ref":"refs/heads/main"}""", "wrong secret");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var unsigned = await CallAsync("""{"ref":"refs/heads/main"}""", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var audit = await _factory.Services.GetRequiredService<AuditLog>().QueryAsync(new AuditQuery(Outcome: AuditOutcome.Denied), ct);
        Assert.Contains(audit.Entries, e => e.Action == "Stacks: webhook" && e.Target == "web" && e.UserName == "webhook");

        var ping = await CallAsync("{}", secret, "ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);

        var otherBranch = await CallAsync("""{"ref":"refs/heads/develop"}""", secret);
        Assert.Equal(HttpStatusCode.Accepted, otherBranch.StatusCode);
        Assert.StartsWith("Ignored:", await otherBranch.Content.ReadAsStringAsync(ct));
        var triggers = _factory.Services.GetRequiredService<GitStackTriggers>();
        Assert.Null(triggers.Pending(stack.Id));

        var push = await CallAsync($$"""{"ref":"refs/heads/main","after":"{{Commit.Replace('0', 'f')}}"}""", secret);
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);
        Assert.Contains("Queued a fetch and redeploy of web", await push.Content.ReadAsStringAsync(ct));
        // The engine is offline: it waits, and the stack's page says so.
        var pending = triggers.Pending(stack.Id);
        Assert.NotNull(pending);
        Assert.Equal("webhook (GitHub)", pending.By);
        var detail = await browser.GetStringAsync("/endpoints/1/stacks/web", ct);
        Assert.Contains("A redeploy asked for by the webhook (GitHub)", detail);
    }

    [Fact]
    public async Task Viewers_see_the_repository_but_not_its_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOffline>();
        await CreateStackAsync();
        await PortFactory.CreateUserAsync(admin, "viewer", "a long enough password", Access.EndpointRole.Viewer);
        var viewer = await _factory.SignInAsync("viewer", "a long enough password");

        var detail = await viewer.GetStringAsync("/endpoints/1/stacks/web", ct);
        Assert.Contains(RepositoryUrl, detail);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/endpoints/1/stacks/web/repository", ct)).StatusCode);
    }
}
