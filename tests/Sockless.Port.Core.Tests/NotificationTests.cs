using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;
using Sockless.Port.Notifications;

namespace Sockless.Port.Core.Tests;

/// <summary>The notifier and alerts over an in-memory database, a fake network and a clock that can be moved.</summary>
internal sealed class NotificationHarness : IAsyncDisposable
{
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public ResourceMonitorTests.ShiftedTime Time { get; } = new();

    public Notifier Notifier { get; private set; } = null!;

    public Alerts Alerts { get; private set; } = null!;

    public EngineConnections Connections { get; } = new();

    public EngineConnection Local { get; } = new(1, "local", new UnixSocketAddress("/fake.sock"), EndpointKind.Local);

    public static async Task<NotificationHarness> CreateAsync()
    {
        var harness = new NotificationHarness();
        await harness.InitializeAsync();
        return harness;
    }

    private async Task InitializeAsync()
    {
        await _db.OpenAsync();
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            await db.Database.MigrateAsync();
            db.Endpoints.Add(new EngineEndpoint { Name = "local", Address = "unix:///fake.sock", Kind = EndpointKind.Local });
            db.Endpoints.Add(new EngineEndpoint { Name = "other", Address = "unix:///other.sock", Kind = EndpointKind.Local });
            await db.SaveChangesAsync();
        }
        Connections.Add(Local);
        var sender = new NotificationSender(new ResourceMonitorTests.FakeEngine(request =>
        {
            lock (Requests)
            {
                Requests.Add(request);
                Bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            }
            return new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? "" : "nope") };
        }));
        Notifier = new Notifier(_services.GetRequiredService<IServiceScopeFactory>(), new EphemeralDataProtectionProvider(), sender, Time, NullLogger<Notifier>.Instance);
        Alerts = new Alerts(Notifier, Connections, Time, NullLogger<Alerts>.Instance);
    }

    public PortDbContext Db() => _services.CreateScope().ServiceProvider.GetRequiredService<PortDbContext>();

    /// <summary>A webhook channel and a rule sending <paramref name="kinds"/> to it.</summary>
    public async Task<(int Channel, int Rule)> WebhookRuleAsync(int quietMinutes = 60, int? endpointId = null, params AlertKind[] kinds)
    {
        var ct = TestContext.Current.CancellationToken;
        var (channel, errors) = await Notifier.SaveChannelAsync(null, "hook", new WebhookTarget("https://hooks.example/x", "X-Token"), "s3cret", false, ct);
        Assert.Empty(errors);
        var (rule, ruleErrors) = await Notifier.SaveRuleAsync(null, new RuleInput("all", true, channel!.Value, endpointId,
            kinds.Length == 0 ? Enum.GetValues<AlertKind>() : kinds, quietMinutes, true, 50, null, 2), ct);
        Assert.Empty(ruleErrors);
        return (channel.Value, rule!.Value);
    }

    /// <summary>Adds a rule sending exits, with no quiet period, to the first channel.</summary>
    public async Task WebhookExitsTooAsync()
    {
        var (_, errors) = await Notifier.SaveRuleAsync(null, new RuleInput("exits", true, 1, null, [AlertKind.ContainerExited], 0, true, null, null, 5),
            TestContext.Current.CancellationToken);
        Assert.Empty(errors);
    }

    /// <summary>Delivers what's queued and returns the titles sent.</summary>
    public async Task<List<string>> SentAsync()
    {
        var before = Bodies.Count;
        await Notifier.DeliverQueuedAsync(TestContext.Current.CancellationToken);
        return [.. Bodies.Skip(before).Select(b => JsonDocument.Parse(b).RootElement.GetProperty("title").GetString()!)];
    }

    public static DockerEvent Container(string action, string name = "web-1", string id = "abc123", string? exitCode = null, bool helper = false)
    {
        var attributes = new Dictionary<string, string> { ["name"] = name, ["image"] = "nginx:1.27" };
        if (exitCode is not null)
            attributes["exitCode"] = exitCode;
        if (helper)
            attributes["sockless-port.helper"] = "volume-browser";
        return new DockerEvent { Type = "container", Action = action, Actor = new EventActor { Id = id, Attributes = attributes } };
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}

public sealed class NotifierTests
{
    [Fact]
    public async Task Channels_are_checked_and_their_secrets_are_stored_encrypted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();

        var (_, errors) = await h.Notifier.SaveChannelAsync(null, "", new WebhookTarget("ftp://x", "bad header!"), null, false, ct);
        Assert.Equal(["Url", "HeaderName", "Name"], errors.Select(e => e.Field));
        Assert.Equal(["Secret"], (await h.Notifier.SaveChannelAsync(null, "push", new GotifyTarget("https://gotify.example"), null, false, ct)).Errors.Select(e => e.Field));
        Assert.Equal(["Port", "From", "To"], (await h.Notifier.SaveChannelAsync(null, "mail",
            new EmailTarget("smtp.example", 0, EmailSecurity.StartTls, null, "not an address", ""), null, false, ct)).Errors.Select(e => e.Field));

        var (id, none) = await h.Notifier.SaveChannelAsync(null, "push", new GotifyTarget("https://gotify.example"), "app-token", false, ct);
        Assert.Empty(none);
        var row = await h.Db().NotificationChannels.SingleAsync(ct);
        Assert.DoesNotContain("app-token", row.ProtectedSecret, StringComparison.Ordinal);
        Assert.True((await h.Notifier.GetChannelAsync(id!.Value, ct))!.HasSecret);

        // Saving without a secret keeps it; a channel's kind can't change.
        Assert.Empty((await h.Notifier.SaveChannelAsync(id, "push", new GotifyTarget("https://gotify2.example"), null, false, ct)).Errors);
        Assert.True((await h.Notifier.GetChannelAsync(id.Value, ct))!.HasSecret);
        Assert.Contains((await h.Notifier.SaveChannelAsync(id, "push", new NtfyTarget("https://ntfy.sh/t"), null, false, ct)).Errors, e => e.Message.Contains("kind can't change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_alert_is_sent_once_until_it_clears_then_resolved_and_held_back_by_the_quiet_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(quietMinutes: 60);
        var alert = new Alert(AlertKind.ContainerUnhealthy, "unhealthy:1:web", 1, "local", AlertSeverity.Warning, "web is unhealthy", "details", "/endpoints/1");

        await h.Notifier.RaiseAsync(alert, ct);
        await h.Notifier.RaiseAsync(alert, ct);
        Assert.Equal(["web is unhealthy"], await h.SentAsync());

        var request = h.Requests[^1];
        Assert.Equal("s3cret", request.Headers.GetValues("X-Token").Single());
        var body = JsonDocument.Parse(h.Bodies[^1]).RootElement;
        Assert.Equal("container-unhealthy", body.GetProperty("kind").GetString());
        Assert.Equal("warning", body.GetProperty("severity").GetString());
        Assert.Equal("local", body.GetProperty("endpoint").GetString());
        // No server address set: no link.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);

        await h.Notifier.ClearAsync(alert.Key, cancellationToken: ct);
        Assert.Equal(["Resolved: web is unhealthy"], await h.SentAsync());

        // Back within the quiet period: held back, and so is its "resolved".
        await h.Notifier.RaiseAsync(alert, ct);
        await h.Notifier.ClearAsync(alert.Key, cancellationToken: ct);
        Assert.Empty(await h.SentAsync());

        h.Time.Shift(TimeSpan.FromMinutes(61));
        Assert.Null(await h.Notifier.SavePublicUrlAsync("https://port.example.com/", ct));
        await h.Notifier.RaiseAsync(alert, ct);
        Assert.Equal(["web is unhealthy"], await h.SentAsync());
        Assert.Equal("https://port.example.com/endpoints/1", JsonDocument.Parse(h.Bodies[^1]).RootElement.GetProperty("url").GetString());

        var history = await h.Notifier.HistoryAsync(1, ct);
        Assert.Equal(3, history.Entries.Count);
        Assert.All(history.Entries, e => Assert.Equal(NotificationOutcome.Sent, e.Outcome));
    }

    [Fact]
    public async Task Rules_choose_by_kind_and_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(endpointId: 2, kinds: [AlertKind.EndpointOffline]);

        await h.Notifier.RaiseAsync(new Alert(AlertKind.EndpointOffline, "offline:1", 1, "local", AlertSeverity.Critical, "local is offline", ""), ct);
        await h.Notifier.RaiseAsync(new Alert(AlertKind.ContainerExited, "exited:2:x", 2, "other", AlertSeverity.Warning, "x exited", ""), ct);
        await h.Notifier.RaiseAsync(new Alert(AlertKind.EndpointOffline, "offline:2", 2, "other", AlertSeverity.Critical, "other is offline", ""), ct);

        Assert.Equal(["other is offline"], await h.SentAsync());
    }

    [Fact]
    public async Task Failed_deliveries_are_recorded_on_the_channel_and_in_the_history()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        var (channel, _) = await h.WebhookRuleAsync();
        h.Status = HttpStatusCode.Forbidden;

        var error = await h.Notifier.SendTestAsync(channel, ct);
        Assert.Equal("hooks.example answered 403 Forbidden: nope", error);
        var info = await h.Notifier.GetChannelAsync(channel, ct);
        Assert.Equal(error, info!.LastError);
        var entry = Assert.Single((await h.Notifier.HistoryAsync(1, ct)).Entries);
        Assert.Equal(NotificationOutcome.Failed, entry.Outcome);
        Assert.True(entry.Test);

        h.Status = HttpStatusCode.OK;
        Assert.Null(await h.Notifier.SendTestAsync(channel, ct));
        Assert.Null((await h.Notifier.GetChannelAsync(channel, ct))!.LastError);

        // Removing the channel removes its rules.
        Assert.True(await h.Notifier.DeleteChannelAsync(channel, ct));
        Assert.Empty(await h.Notifier.RuleInfosAsync(ct));
    }

    [Fact]
    public async Task Ntfy_and_gotify_get_their_own_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        Assert.Null(await h.Notifier.SavePublicUrlAsync("https://port.example.com", ct));
        var (ntfy, _) = await h.Notifier.SaveChannelAsync(null, "phone", new NtfyTarget("https://ntfy.sh/ops"), "tk_abc", false, ct);
        var (gotify, _) = await h.Notifier.SaveChannelAsync(null, "gotify", new GotifyTarget("https://gotify.example/"), "app-token", false, ct);

        Assert.Null(await h.Notifier.SendTestAsync(ntfy!.Value, ct));
        var request = h.Requests[^1];
        Assert.Equal("https://ntfy.sh/ops", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Test notification from Sockless Port", request.Headers.GetValues("Title").Single());
        Assert.Equal("https://port.example.com/notifications", request.Headers.GetValues("Click").Single());
        Assert.Equal("Bearer tk_abc", request.Headers.Authorization!.ToString());
        Assert.Equal("If you can read this, the channel works.", h.Bodies[^1]);

        Assert.Null(await h.Notifier.SendTestAsync(gotify!.Value, ct));
        request = h.Requests[^1];
        Assert.Equal("https://gotify.example/message", request.RequestUri!.AbsoluteUri);
        Assert.Equal("app-token", request.Headers.GetValues("X-Gotify-Key").Single());
        var body = JsonDocument.Parse(h.Bodies[^1]).RootElement;
        Assert.Equal("https://port.example.com/notifications", body.GetProperty("extras").GetProperty("client::notification").GetProperty("click").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Rules_are_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        var (_, errors) = await h.Notifier.SaveRuleAsync(null, new RuleInput("", true, 99, 42, [AlertKind.ResourceUsage], 7, true, null, null, 0), ct);
        Assert.Equal(["Name", "ChannelId", "EndpointId", "QuietMinutes", "CpuPercent", "ForMinutes"], errors.Select(e => e.Field));
    }
}

public sealed class AlertTests
{
    [Fact]
    public async Task Containers_alert_when_they_exit_with_an_error_unless_asked_to_stop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(quietMinutes: 0);

        // docker stop: kill, then die with 143.
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("kill"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "143"), ct);
        // A clean exit and helpers don't alert.
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "0"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", name: "browser", id: "h1", exitCode: "1", helper: true), ct);
        Assert.Empty(await h.SentAsync());

        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "137"), ct);
        Assert.Equal(["web-1 exited with code 137 on local"], await h.SentAsync());
        Assert.Contains("SIGKILL", JsonDocument.Parse(h.Bodies[^1]).RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("start"), ct);
        Assert.Equal(["Resolved: web-1 exited with code 137 on local"], await h.SentAsync());
    }

    [Fact]
    public async Task Running_out_of_memory_alerts_even_after_a_kill()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync();

        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("oom"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("kill"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "137"), ct);
        Assert.Equal(["web-1 ran out of memory on local"], await h.SentAsync());
    }

    [Fact]
    public async Task Repeated_deaths_are_a_restart_loop_that_clears_when_quiet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(kinds: [AlertKind.ContainerRestarting]);

        for (var i = 0; i < Alerts.RestartCount; i++)
        {
            await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "1"), ct);
            await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("start"), ct);
        }
        Assert.Equal(["web-1 keeps restarting on local"], await h.SentAsync());

        // Its exits are part of the loop now, and say nothing more.
        await h.WebhookExitsTooAsync();
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("die", exitCode: "1"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("start"), ct);
        Assert.Empty(await h.SentAsync());

        await h.Alerts.SweepAsync(ct);
        Assert.Empty(await h.SentAsync());
        h.Time.Shift(Alerts.RestartWindow + TimeSpan.FromMinutes(1));
        await h.Alerts.SweepAsync(ct);
        Assert.Equal(["Resolved: web-1 keeps restarting on local"], await h.SentAsync());
    }

    [Fact]
    public async Task Health_checks_alert_and_resolve_and_removed_containers_are_forgotten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync();

        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("health_status: unhealthy"), ct);
        Assert.Equal(["web-1 is unhealthy on local"], await h.SentAsync());
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("health_status: healthy"), ct);
        Assert.Equal(["Resolved: web-1 is unhealthy on local"], await h.SentAsync());

        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("health_status: unhealthy", name: "db"), ct);
        await h.Alerts.EngineEventAsync(h.Local, NotificationHarness.Container("destroy", name: "db"), ct);
        Assert.Equal(["db is unhealthy on local"], await h.SentAsync());
        Assert.Empty(h.Notifier.Active);
    }

    [Fact]
    public async Task Endpoints_alert_after_the_grace_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync();

        var now = h.Time.GetUtcNow();
        h.Local.SetOffline("Can't reach the engine.", now, now + TimeSpan.FromSeconds(5));
        await h.Alerts.StateChangedAsync(h.Local, ct);
        await h.Alerts.SweepAsync(ct);
        Assert.Empty(await h.SentAsync());

        h.Time.Shift(Alerts.OfflineGrace);
        await h.Alerts.SweepAsync(ct);
        Assert.Equal(["local is offline"], await h.SentAsync());
        Assert.Contains("Can't reach the engine.", JsonDocument.Parse(h.Bodies[^1]).RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_thresholds_need_their_minutes_in_a_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(kinds: [AlertKind.ResourceUsage]);

        // 4 CPUs: 240 is 60% of the host; the rule's limit is 50% for 2 minutes.
        static ResourceTotalEntry Totals(double cpu) => new() { EndpointId = 1, Cpus = 4, HostMemory = 1_000, Cpu = cpu, Memory = 100 };
        static ResourceUsageEntry[] Web(double cpu) => [new() { EndpointId = 1, Name = "web", Cpu = cpu, Memory = 50 }];

        await h.Alerts.ResourceMinuteAsync(h.Local, Totals(240), Web(240), ct);
        Assert.Empty(await h.SentAsync());
        await h.Alerts.ResourceMinuteAsync(h.Local, Totals(240), Web(100), ct);
        Assert.Equal(["All containers on local: CPU above 50%"], await h.SentAsync());
        await h.Alerts.ResourceMinuteAsync(h.Local, Totals(80), Web(80), ct);
        Assert.Equal(["Resolved: All containers on local: CPU above 50%"], await h.SentAsync());
    }

    [Fact]
    public async Task Updates_alert_when_the_list_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(kinds: [AlertKind.UpdatesAvailable]);

        await h.Alerts.UpdatesCheckedAsync(h.Local, ["nginx:1.27", "redis:7"], ct);
        await h.Alerts.UpdatesCheckedAsync(h.Local, ["redis:7", "nginx:1.27"], ct);
        Assert.Equal(["2 images have updates on local"], await h.SentAsync());
        await h.Alerts.UpdatesCheckedAsync(h.Local, ["redis:7"], ct);
        Assert.Equal(["1 image has an update on local"], await h.SentAsync());
        await h.Alerts.UpdatesCheckedAsync(h.Local, [], ct);
        Assert.Empty(await h.SentAsync());
    }

    [Fact]
    public async Task New_vulnerabilities_are_counted_by_severity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await NotificationHarness.CreateAsync();
        await h.WebhookRuleAsync(kinds: [AlertKind.Vulnerabilities]);

        await h.Alerts.NewVulnerabilitiesAsync(h.Local, "sha256:abc", "nginx:1.27", [("CVE-1", "Critical"), ("CVE-2", "High"), ("CVE-3", "High")], ct);
        Assert.Equal(["nginx:1.27 has 1 critical and 2 high new vulnerabilities"], await h.SentAsync());
        Assert.Equal("critical", JsonDocument.Parse(h.Bodies[^1]).RootElement.GetProperty("severity").GetString());
    }
}
