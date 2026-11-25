using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Data;
using Sockless.Port.Maintenance;

namespace Sockless.Port.Web.Tests;

/// <summary>The maintenance page, scheduled prunes and scheduled backups through the pages. The local endpoint stays offline.</summary>
public sealed class MaintenancePagesTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    private async Task<List<(string Action, string? Target, string? Detail)>> AuditAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return [.. (await db.AuditEntries.OrderBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(e => (e.Action, e.Target, e.Detail))];
    }

    [Fact]
    public async Task Backups_are_scheduled_written_listed_downloaded_restored_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();

        var empty = await admin.GetStringAsync("/maintenance", ct);
        Assert.Contains("Off: only when asked", empty);
        Assert.Contains("No backups in the folder yet", empty);
        Assert.Contains("href=\"/maintenance\"", empty);

        var mismatch = await PostAsync(admin, "/settings", "/settings?handler=BackupSchedule", new()
        {
            ["BackupIntervalHours"] = "24",
            ["BackupKeep"] = "5",
            ["BackupFolder"] = "",
            ["BackupEncrypt"] = "true",
            ["BackupPassphrase"] = Passphrase,
            ["BackupPassphraseConfirm"] = "something else entirely",
        });
        Assert.Equal(HttpStatusCode.OK, mismatch.StatusCode);
        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync(ct));

        var saved = await PostAsync(admin, "/settings", "/settings?handler=BackupSchedule", new()
        {
            ["BackupIntervalHours"] = "24",
            ["BackupKeep"] = "5",
            ["BackupFolder"] = "",
            ["BackupEncrypt"] = "true",
            ["BackupPassphrase"] = Passphrase,
            ["BackupPassphraseConfirm"] = Passphrase,
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Leave empty to keep the stored one", await admin.GetStringAsync("/settings", ct));

        var now = await PostAsync(admin, "/maintenance", "/maintenance?handler=BackupNow", []);
        Assert.Equal(HttpStatusCode.Redirect, now.StatusCode);
        var page = await admin.GetStringAsync("/maintenance", ct);
        Assert.Contains("Every day, keeping the newest 5", page);
        Assert.Contains("With the stored passphrase", page);
        Assert.Contains("Worked", page);
        Assert.Contains(", encrypted to", page);
        var name = Directory.GetFiles(Path.Combine(_factory.DataPath, "backups")).Select(Path.GetFileName).Single()!;
        Assert.EndsWith(".tar.gz.enc", name);
        Assert.Contains(name, page);

        var download = await PostAsync(admin, "/maintenance", "/maintenance?handler=Download", new() { ["name"] = name });
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal("SPBKENC1"u8.ToArray(), bytes[..8]);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, "/maintenance", "/maintenance?handler=Download", new() { ["name"] = "../port.db" })).StatusCode);

        // An encrypted upload needs its passphrase.
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");
        await admin.PostAsync("/settings?handler=Restore", Upload(csrf, bytes, name, null), ct);
        Assert.Contains("This backup is encrypted. Enter its passphrase.", await admin.GetStringAsync("/settings", ct));
        await admin.PostAsync("/settings?handler=Restore", Upload(csrf, bytes, name, Passphrase), ct);
        Assert.Contains("is ready to restore", await admin.GetStringAsync("/settings", ct));
        await PostAsync(admin, "/settings", "/settings?handler=CancelRestore", []);

        // A stored one restores with the stored passphrase.
        var restore = await PostAsync(admin, "/maintenance", "/maintenance?handler=Restore", new() { ["name"] = name });
        Assert.Equal("/settings", restore.Headers.Location?.OriginalString);
        Assert.True(Directory.Exists(Path.Combine(_factory.DataPath, "restore-pending")));
        await PostAsync(admin, "/settings", "/settings?handler=CancelRestore", []);

        var delete = await PostAsync(admin, "/maintenance", "/maintenance?handler=Delete", new() { ["name"] = name });
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_factory.DataPath, "backups")));

        var audit = await AuditAsync();
        Assert.Contains(audit, e => e.Action == "Settings: backup schedule");
        Assert.Contains(audit, e => e.Action == "Maintenance: back up" && e.Target == name);
        Assert.Contains(audit, e => e.Action == "Maintenance: download" && e.Target == name);
        Assert.Contains(audit, e => e.Action == "Maintenance: restore" && e.Target == name);
        Assert.Contains(audit, e => e.Action == "Maintenance: delete" && e.Target == name);
        Assert.DoesNotContain(audit, e => (e.Detail ?? "").Contains(Passphrase, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prunes_are_scheduled_per_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await _factory.CreateSignedInBrowserAsync();
        var id = _factory.LocalEndpoint.Id;
        var form = $"/maintenance/prunes/{id}";

        Assert.Contains("Not scheduled", await admin.GetStringAsync("/maintenance", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/maintenance/prunes/999", ct)).StatusCode);

        var nothing = await PostAsync(admin, form, form, new()
        {
            ["IntervalHours"] = "24",
            ["Images"] = "None",
            ["OlderThanDays"] = "0",
            ["Volumes"] = "None",
            ["Networks"] = "false",
            ["BuildCache"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        Assert.Contains("Choose something to remove", await nothing.Content.ReadAsStringAsync(ct));

        var saved = await PostAsync(admin, form, form, new()
        {
            ["IntervalHours"] = "168",
            ["Images"] = "Unused",
            ["OlderThanDays"] = "14",
            ["Volumes"] = "Anonymous",
            ["Networks"] = "true",
            ["BuildCache"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var page = await admin.GetStringAsync("/maintenance", ct);
        Assert.Contains("Unused images older than 14 days, unused anonymous volumes, unused networks", page);
        Assert.Contains("Every week", page);
        Assert.Contains("When it's online", page);
        var schedule = await _factory.Services.GetRequiredService<PruneSchedules>().GetAsync(id, ct);
        Assert.Equal(new PruneOptions(PruneImages.Unused, 14, PruneVolumes.Anonymous, true, false), schedule!.Options);

        // The endpoint is offline, so it can't run now.
        await PostAsync(admin, "/maintenance", $"/maintenance?handler=PruneNow&id={id}", []);
        Assert.Contains("is offline", await admin.GetStringAsync("/maintenance", ct));

        var audit = await AuditAsync();
        Assert.Contains(audit, e => e.Action == "Maintenance: change prune schedule" && e.Target == _factory.LocalEndpoint.Name);
        Assert.DoesNotContain(audit, e => e.Action == "Maintenance: prune");
    }

    private static MultipartFormDataContent Upload(string token, byte[] bytes, string fileName, string? passphrase)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { file, "Backup", fileName },
        };
        if (passphrase is not null)
            content.Add(new StringContent(passphrase), "RestorePassphrase");
        return content;
    }
}
