using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Notifications;

namespace Sockless.Port.Web.Tests;

/// <summary>Channels and rules through the pages. Deliveries go to an address where nothing listens.</summary>
public sealed class NotificationPagesTests : IDisposable
{
    private const string Password = "a long enough password";
    private const string NowhereUrl = "http://127.0.0.1:9/hook";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Channels_and_rules_are_added_tested_edited_and_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();

        var empty = await admin.GetStringAsync("/notifications", ct);
        Assert.Contains("No channels yet", empty);
        Assert.Contains("href=\"/notifications/channels?kind=ntfy\"", empty);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/notifications/channels?kind=pigeon", ct)).StatusCode);

        // Without channels, a rule can't be added.
        var noChannel = await admin.GetAsync("/notifications/rules", ct);
        Assert.Equal(HttpStatusCode.Redirect, noChannel.StatusCode);

        var bad = await PostAsync(admin, "/notifications/channels?kind=webhook", "/notifications/channels?kind=webhook", new()
        {
            ["Form.Name"] = "hook", ["Form.Url"] = "not a url",
        });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Enter the webhook&#x27;s http:// or https:// address.", await bad.Content.ReadAsStringAsync(ct));

        var added = await PostAsync(admin, "/notifications/channels?kind=webhook", "/notifications/channels?kind=webhook", new()
        {
            ["Form.Name"] = "hook", ["Form.Url"] = NowhereUrl, ["Form.HeaderName"] = "X-Token", ["Form.Secret"] = "s3cret",
        });
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        var notifier = _factory.Services.GetRequiredService<Notifier>();
        var channel = Assert.Single(await notifier.ChannelsAsync(ct));
        Assert.True(channel.HasSecret);

        // The edit form never shows the secret.
        var edit = await admin.GetStringAsync($"/notifications/channels/{channel.Id}", ct);
        Assert.DoesNotContain("s3cret", edit);
        Assert.Contains("Saved; enter a new one to change it", edit);

        var tested = await PostAsync(admin, "/notifications", $"/notifications?handler=Test&id={channel.Id}", []);
        Assert.Equal(HttpStatusCode.Redirect, tested.StatusCode);
        var afterTest = await admin.GetStringAsync("/notifications", ct);
        Assert.Contains("Couldn&#x27;t send a test to hook", afterTest);
        Assert.Contains("Failing", afterTest);

        var rule = await PostAsync(admin, $"/notifications/rules", "/notifications/rules", new()
        {
            ["Form.Name"] = "problems", ["Form.ChannelId"] = channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Form.EndpointId"] = "",
            ["Form.Kinds"] = "ContainerUnhealthy", ["Form.QuietMinutes"] = "60", ["Form.Enabled"] = "true", ["Form.SendResolved"] = "true",
            ["Form.CpuPercent"] = "", ["Form.MemoryPercent"] = "", ["Form.ForMinutes"] = "5",
        });
        Assert.Equal(HttpStatusCode.Redirect, rule.StatusCode);
        var saved = Assert.Single(await notifier.RuleInfosAsync(ct));
        Assert.Equal([AlertKind.ContainerUnhealthy], saved.Kinds);
        Assert.Null(saved.EndpointId);

        var noKinds = await PostAsync(admin, $"/notifications/rules/{saved.Id}", $"/notifications/rules/{saved.Id}", new()
        {
            ["Form.Name"] = "problems", ["Form.ChannelId"] = channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Form.QuietMinutes"] = "60",
        });
        Assert.Contains("Choose at least one thing to send.", await noKinds.Content.ReadAsStringAsync(ct));

        var list = await admin.GetStringAsync("/notifications", ct);
        Assert.Contains(">problems</a>", list);
        Assert.Contains("a container turns unhealthy", list);

        var removed = await PostAsync(admin, $"/notifications/channels/{channel.Id}", $"/notifications/channels/{channel.Id}?handler=Delete", []);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Empty(await notifier.RuleInfosAsync(ct));

        var audit = await _factory.Services.GetRequiredService<AuditLog>().QueryAsync(new AuditQuery(Search: "Notifications"), ct);
        Assert.Contains(audit.Entries, e => e.Action == "Notifications: add channel" && e.Target == "hook");
        Assert.Contains(audit.Entries, e => e.Action == "Notifications: test channel" && e.Outcome == AuditOutcome.Failed);
        Assert.Contains(audit.Entries, e => e.Action == "Notifications: remove channel" && e.Target == "hook");
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_server_address_is_checked_and_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();

        var bad = await PostAsync(admin, "/notifications", "/notifications?handler=PublicUrl", new() { ["PublicUrl"] = "port.example.com" });
        Assert.Contains("Enter the address people open Sockless Port at", await bad.Content.ReadAsStringAsync(ct));

        var good = await PostAsync(admin, "/notifications", "/notifications?handler=PublicUrl", new() { ["PublicUrl"] = "https://port.example.com/" });
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.Equal("https://port.example.com", await _factory.Services.GetRequiredService<Notifier>().PublicUrlAsync(ct));
    }

    [Fact]
    public async Task Only_administrators_see_notifications()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();
        Assert.Contains("href=\"/notifications\"", await admin.GetStringAsync("/endpoints", ct));

        await PortFactory.CreateUserAsync(admin, "opal", Password, EndpointRole.Operator);
        var operatorBrowser = await _factory.SignInAsync("opal", Password);
        Assert.NotEqual(HttpStatusCode.OK, (await operatorBrowser.GetAsync("/notifications", ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await operatorBrowser.GetAsync("/notifications/channels?kind=webhook", ct)).StatusCode);
        Assert.DoesNotContain("href=\"/notifications\"", await operatorBrowser.GetStringAsync("/endpoints", ct));
    }
}
