using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Audit;
using Sockless.Port.Backup;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Maintenance;
using Sockless.Port.Notifications;
using Sockless.Port.Registries;
using Sockless.Port.Volumes;
using static Sockless.Port.Core.Tests.ResourceMonitorTests;

namespace Sockless.Port.Core.Tests;

/// <summary>Backup encryption, scheduled backups, prunes and the maintenance schedule, over an in-memory database.</summary>
public sealed class MaintenanceTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"maintenance-{Guid.NewGuid():n}");
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly ShiftedTime _time = new();
    private readonly EphemeralDataProtectionProvider _protection = new();
    private ServiceProvider _services = null!;
    private NotificationHarness _notifications = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataPath);
        await _db.OpenAsync();
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        await db.Database.MigrateAsync();
        db.Endpoints.Add(new EngineEndpoint { Name = "local", Address = "unix:///fake.sock", Kind = EndpointKind.Local });
        await db.SaveChangesAsync();
        _notifications = await NotificationHarness.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _notifications.DisposeAsync();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

    private IOptions<PortOptions> Options => Microsoft.Extensions.Options.Options.Create(new PortOptions { DataPath = _dataPath });

    private MaintenanceLog Log() => new(Scopes, NullLogger<MaintenanceLog>.Instance);

    private AuditLog Audit() => new(Scopes, _time, NullLogger<AuditLog>.Instance);

    private ScheduledBackups Backups(Alerts? alerts = null) =>
        new(new DataBackup(Options, Scopes, _time, NullLogger<DataBackup>.Instance), Scopes, _protection, Options, Log(), Audit(), _time,
            NullLogger<ScheduledBackups>.Instance, alerts: alerts);

    private async Task<T> ReadDbAsync<T>(Func<PortDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<PortDbContext>());
    }

    // ---------------------------------------------------------------- encryption

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(BackupCipher.ChunkSize)]
    [InlineData(BackupCipher.ChunkSize * 2 + 17)]
    public async Task Encrypted_data_reads_back_with_the_passphrase(int length)
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = RandomNumberGenerator.GetBytes(length);

        var encrypted = await EncryptAsync(plain);
        var read = new MemoryStream();
        await using (var stream = await BackupCipher.OpenReadAsync(new MemoryStream(encrypted), Passphrase, ct))
            await stream.CopyToAsync(read, ct);

        Assert.Equal(plain, read.ToArray());
        Assert.Equal("SPBKENC1"u8.ToArray(), encrypted[..8]);
    }

    [Fact]
    public async Task A_wrong_passphrase_or_a_changed_file_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(BackupCipher.ChunkSize + 5));

        async Task<string> ReadAsync(byte[] bytes, string? passphrase)
        {
            var ex = await Assert.ThrowsAsync<BackupRejectedException>(async () =>
            {
                await using var stream = await BackupCipher.OpenReadAsync(new MemoryStream(bytes), passphrase, ct);
                await stream.CopyToAsync(Stream.Null, ct);
            });
            return ex.Message;
        }

        Assert.Contains("passphrase is wrong", await ReadAsync(encrypted, "another long passphrase"));
        Assert.Contains("Enter its passphrase", await ReadAsync(encrypted, null));
        // The last record dropped: the one before it isn't marked as last.
        var firstRecord = BackupCipher.HeaderSize + 4 + BackupCipher.ChunkSize + BackupCipher.TagSize;
        Assert.Contains("cut short", await ReadAsync(encrypted[..firstRecord], Passphrase));
        Assert.Contains("data after its end", await ReadAsync([.. encrypted, 0], Passphrase));
        var flipped = encrypted.ToArray();
        flipped[^1] ^= 1;
        Assert.Contains("damaged", await ReadAsync(flipped, Passphrase));
    }

    [Fact]
    public async Task An_unencrypted_backup_reads_as_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = "\x1f\x8b plain gzip"u8.ToArray();
        var read = new MemoryStream();
        await using (var stream = await BackupCipher.OpenReadAsync(new MemoryStream(plain), "ignored passphrase", ct))
            await stream.CopyToAsync(read, ct);
        Assert.Equal(plain, read.ToArray());
    }

    private static async Task<byte[]> EncryptAsync(byte[] plain)
    {
        var output = new MemoryStream();
        await using (var cipher = BackupCipher.Encrypt(output, Passphrase, leaveOpen: true, iterations: 100_000))
        {
            // In odd pieces, so records are cut across writes.
            for (var i = 0; i < plain.Length; i += 7000)
                await cipher.WriteAsync(plain.AsMemory(i, Math.Min(7000, plain.Length - i)));
        }
        return output.ToArray();
    }

    // ---------------------------------------------------------------- scheduled backups

    [Fact]
    public async Task The_backup_schedule_is_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        var backups = Backups();

        var errors = await backups.SaveAsync(12, 0, "stacks/backups", encrypt: true, passphrase: null, ct);
        Assert.Equal(["BackupIntervalHours", "BackupKeep", "BackupFolder", "BackupPassphrase"], errors.Select(e => e.Field));
        Assert.Contains("manages", errors[2].Message);
        Assert.Contains("at least", (await backups.SaveAsync(24, 3, "", encrypt: true, passphrase: "short", ct)).Single().Message);
        Assert.Contains("not the data folder", (await backups.SaveAsync(24, 3, ".", encrypt: false, passphrase: null, ct)).Single().Message);

        Assert.Empty(await backups.SaveAsync(24, 3, "", encrypt: true, passphrase: Passphrase, ct));
        Assert.Equal(new BackupSchedule(24, 3, "", Encrypted: true), await backups.GetAsync(ct));
        // The passphrase is stored encrypted, and kept when the form leaves it empty.
        Assert.DoesNotContain(await ReadDbAsync(db => db.Settings.Select(s => s.Value).ToListAsync(ct)), v => v.Contains(Passphrase, StringComparison.Ordinal));
        Assert.Empty(await backups.SaveAsync(168, 3, "elsewhere", encrypt: true, passphrase: null, ct));
        Assert.Equal(Passphrase, await backups.PassphraseAsync(ct));
        Assert.Empty(await backups.SaveAsync(168, 3, "elsewhere", encrypt: false, passphrase: null, ct));
        Assert.Null(await backups.PassphraseAsync(ct));
        Assert.Equal(Path.GetFullPath(Path.Combine(_dataPath, "elsewhere")), backups.ResolveFolder("elsewhere"));
    }

    [Fact]
    public async Task Scheduled_backups_are_written_encrypted_kept_to_a_count_and_restored()
    {
        var ct = TestContext.Current.CancellationToken;
        await _notifications.WebhookRuleAsync(0, null, AlertKind.BackupFailed);
        var backups = Backups(_notifications.Alerts);
        Assert.Empty(await backups.SaveAsync(24, 2, "", encrypt: true, passphrase: Passphrase, ct));
        // Something outside what backups manage stays.
        var folder = backups.ResolveFolder("");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "mine", ct);

        for (var i = 0; i < 3; i++)
        {
            var result = await backups.RunAsync(MaintenanceTrigger.Schedule, ct);
            Assert.Equal(MaintenanceOutcome.Succeeded, result.Outcome);
            Assert.EndsWith(".tar.gz.enc", result.FileName);
            _time.Shift(TimeSpan.FromMinutes(1));
        }

        var (_, files) = await backups.FilesAsync(ct);
        Assert.Equal(2, files.Count);
        Assert.True(string.CompareOrdinal(files[0].Name, files[1].Name) > 0);
        Assert.All(files, f => Assert.True(f.Encrypted));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        var runs = await ReadDbAsync(db => db.MaintenanceRuns.ToListAsync(ct));
        Assert.Equal(3, runs.Count);
        Assert.Contains("Deleted the oldest", runs[^1].Summary);
        Assert.Equal(3, await ReadDbAsync(db => db.AuditEntries.CountAsync(e => e.Action == "Maintenance: back up" && e.UserName == "schedule", ct)));
        Assert.Empty(await _notifications.SentAsync());

        // The newest restores with its passphrase.
        await using (var stream = await backups.OpenAsync(files[0].Name, ct))
        {
            var staged = await new DataBackup(Options, Scopes, _time, NullLogger<DataBackup>.Instance).StageAsync(stream!, Passphrase, ct);
            Assert.True(staged.Files > 0);
        }
        Assert.True(File.Exists(Path.Combine(_dataPath, "restore-pending", "port.db")));
        Assert.Null(await backups.OpenAsync("../port.db", ct));
        Assert.False(await backups.DeleteAsync("notes.txt", ct));
        Assert.True(await backups.DeleteAsync(files[1].Name, ct));
    }

    [Fact]
    public async Task A_failed_backup_alerts_once_and_is_tried_again_after_an_hour()
    {
        var ct = TestContext.Current.CancellationToken;
        await _notifications.WebhookRuleAsync(0, null, AlertKind.BackupFailed);
        var backups = Backups(_notifications.Alerts);
        Assert.Empty(await backups.SaveAsync(24, 2, "out", encrypt: false, passphrase: null, ct));
        // A file where the folder should be.
        var folder = backups.ResolveFolder("out");
        Directory.Delete(folder);
        await File.WriteAllTextAsync(folder, "in the way", ct);

        var failed = await backups.RunAsync(MaintenanceTrigger.Schedule, ct);
        await backups.RunAsync(MaintenanceTrigger.Schedule, ct);
        Assert.Equal(MaintenanceOutcome.Failed, failed.Outcome);
        Assert.Contains("Couldn't write a backup", failed.Summary);
        Assert.Equal(["A backup of Sockless Port failed"], await _notifications.SentAsync());
        Assert.Contains("\"resolved\":false", _notifications.Bodies[^1]);

        var schedule = await backups.GetAsync(ct);
        var next = await backups.NextAsync(schedule, ct);
        Assert.InRange(next!.Value - _time.GetUtcNow(), TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        File.Delete(folder);
        Assert.Equal(MaintenanceOutcome.Succeeded, (await backups.RunAsync(MaintenanceTrigger.Schedule, ct)).Outcome);
        Assert.Single(await _notifications.SentAsync());
        Assert.Contains("\"resolved\":true", _notifications.Bodies[^1]);
        Assert.InRange((await backups.NextAsync(schedule, ct))!.Value - _time.GetUtcNow(), TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
    }

    // ---------------------------------------------------------------- prunes

    [Fact]
    public async Task A_prune_removes_each_part_and_records_what_it_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, requests) = await EngineAsync(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.51/containers/json" => Json("[]"),
            "/v1.51/volumes/prune" => Json("""{"VolumesDeleted":["abc123","def456"],"SpaceReclaimed":2000}"""),
            "/v1.51/networks/prune" => Json("""{"NetworksDeleted":["old_default"]}"""),
            "/v1.51/images/prune" => Json("""{"ImagesDeleted":[{"Untagged":"nginx:1.25"},{"Deleted":"sha256:aaa"},{"Deleted":"sha256:bbb"}],"SpaceReclaimed":3000000}"""),
            "/v1.51/build/prune" => Json("""{"CachesDeleted":["x","y","z"],"SpaceReclaimed":500}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var runner = Runner();

        var result = await runner.RunAsync(connection, connection.Client!, new PruneOptions(PruneImages.Unused, 7, PruneVolumes.Anonymous, true, true),
            new MaintenanceTrigger("ann", "u1", "10.0.0.1"), ct);

        Assert.True(result.Outcome == MaintenanceOutcome.Succeeded, result.Summary);
        Assert.Equal("Removed 2 volumes (abc123, def456); 1 network (old_default); 2 images or layers and 1 tag (nginx:1.25); 3 build cache entries. Freed 3 MB.",
            result.Summary);
        Assert.Equal([
            // The file browser's helpers are released before volumes go.
            """/v1.51/containers/json all=true {"label":["sockless-port.helper=volume-browser"]}""",
            "/v1.51/volumes/prune ",
            "/v1.51/networks/prune ",
            """/v1.51/images/prune {"dangling":["false"],"until":["168h"]}""",
            """/v1.51/build/prune all=true {"until":["168h"]}""",
        ], requests);
        var audit = await ReadDbAsync(db => db.AuditEntries.SingleAsync(ct));
        Assert.Equal(("ann", "Maintenance: prune", "local", AuditOutcome.Succeeded), (audit.UserName, audit.Action, audit.EndpointName, audit.Outcome));
        Assert.Equal("unused images older than 7 days, unused anonymous volumes, unused networks, build cache older than 7 days", audit.Target);
        Assert.Equal(result.Summary, audit.Detail);
        var run = await ReadDbAsync(db => db.MaintenanceRuns.SingleAsync(ct));
        Assert.Equal((MaintenanceTask.Prune, 1, "ann"), (run.Task, run.EndpointId!.Value, run.By));
    }

    [Fact]
    public async Task A_failed_part_does_not_stop_the_others_and_raises_an_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        await _notifications.WebhookRuleAsync(0, null, AlertKind.TaskFailed);
        var (connection, requests) = await EngineAsync(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.51/images/prune" => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"message":"a prune operation is already running"}""") },
            "/v1.51/networks/prune" => Json("""{"NetworksDeleted":null}"""),
            // Podman has no build cache API.
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"page not found"}""") },
        });
        var runner = Runner(_notifications.Alerts);

        var result = await runner.RunAsync(connection, connection.Client!, new PruneOptions(PruneImages.Dangling, 0, PruneVolumes.None, true, true),
            MaintenanceTrigger.Schedule, ct);

        Assert.Equal(MaintenanceOutcome.Failed, result.Outcome);
        Assert.StartsWith("Images: A prune operation is already running.", result.Summary);
        Assert.Contains("no build cache", result.Summary);
        Assert.Equal(3, requests.Count);
        Assert.Equal(["The scheduled prune of local failed"], await _notifications.SentAsync());
        Assert.Equal(AuditOutcome.Failed, (await ReadDbAsync(db => db.AuditEntries.SingleAsync(ct))).Outcome);
    }

    [Fact]
    public async Task Old_engines_never_lose_named_volumes_to_an_anonymous_prune()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, requests) = await EngineAsync(_ => Json("{}"), apiVersion: "1.41");

        var result = await Runner().RunAsync(connection, connection.Client!, new PruneOptions(PruneImages.None, 0, PruneVolumes.Anonymous, false, false),
            MaintenanceTrigger.Schedule, ct);

        Assert.Equal(MaintenanceOutcome.Failed, result.Outcome);
        Assert.Contains("would remove named volumes too", result.Summary);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Prune_schedules_are_checked_saved_and_run_when_due()
    {
        var ct = TestContext.Current.CancellationToken;
        var schedules = new PruneSchedules(Scopes, _time);
        var nothing = new PruneOptions(PruneImages.None, 0, PruneVolumes.None, false, false);
        Assert.Equal(["IntervalHours", "OlderThanDays"], (await schedules.SaveAsync(1, 12, nothing with { OlderThanDays = -1 }, ct)).Select(e => e.Field));
        Assert.Contains("Choose something", (await schedules.SaveAsync(1, 24, nothing, ct)).Single().Message);
        // Off with nothing chosen is fine.
        Assert.Empty(await schedules.SaveAsync(1, 0, nothing, ct));
        Assert.Empty(await schedules.SaveAsync(1, 24, nothing with { Networks = true }, ct));
        Assert.Equal(1, await ReadDbAsync(db => db.PruneSchedules.CountAsync(ct)));

        var (connection, requests) = await EngineAsync(_ => Json("""{"NetworksDeleted":[]}"""));
        var connections = new EngineConnections();
        connections.Add(connection);
        var schedule = new MaintenanceSchedule(Backups(), schedules, Runner(), Log(), connections, _time, NullLogger<MaintenanceSchedule>.Instance);

        await schedule.RunDueAsync(ct);
        await schedule.RunDueAsync(ct);
        Assert.Single(requests);
        _time.Shift(TimeSpan.FromHours(24));
        await schedule.RunDueAsync(ct);
        Assert.Equal(2, requests.Count);
        // Backups are off, so none was written.
        Assert.False(Directory.Exists(Path.Combine(_dataPath, "backups")));
    }

    [Fact]
    public void The_next_prune_follows_the_last_run()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new PruneSchedule { IntervalHours = 168, Networks = true };
        Assert.Equal(now, MaintenanceSchedule.NextPrune(schedule, null, now));
        Assert.Equal(now.AddDays(6), MaintenanceSchedule.NextPrune(schedule, new MaintenanceRun { StartedAt = now.AddDays(-1), By = "x", Summary = "" }, now));
        Assert.Null(MaintenanceSchedule.NextPrune(new PruneSchedule { IntervalHours = 0, Networks = true }, null, now));
        Assert.Null(MaintenanceSchedule.NextPrune(new PruneSchedule { IntervalHours = 24 }, null, now));
    }

    private PruneRunner Runner(Alerts? alerts = null)
    {
        var browser = new VolumeBrowser(Options, new RegistryCredentials(Scopes, _protection, _time, NullLogger<RegistryCredentials>.Instance), _time, NullLogger<VolumeBrowser>.Instance);
        return new PruneRunner(browser, Log(), Audit(), _time, NullLogger<PruneRunner>.Instance, alerts: alerts);
    }

    /// <summary>An online endpoint 1 ("local") whose engine answers prunes with <paramref name="respond"/>; requests are listed as path, query.</summary>
    private static async Task<(EngineConnection Connection, List<string> Requests)> EngineAsync(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string apiVersion = "1.51")
    {
        List<string> requests = [];
        var handler = new FakeEngine(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/version")
                return Json($$"""{"ApiVersion":"{{apiVersion}}"}""");
            // "path all=true {filters}": other parameters first, then the filters.
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var parts = query.AllKeys.OfType<string>().Where(k => k != "filters").Select(k => $"{k}={query[k]}").Append(query["filters"]).OfType<string>();
            lock (requests)
                requests.Add($"{path} {string.Join(" ", parts)}");
            return respond(request);
        });
        var client = await DockerClient.ConnectAsync(handler, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, TestContext.Current.CancellationToken);
        var connection = new EngineConnection(1, "local", new UnixSocketAddress("/fake.sock"), EndpointKind.Local);
        connection.SetOnline(client, new EngineDescriptor(EngineKind.Docker, "29.0.0", client.ApiVersion, "linux", "amd64", EngineCapabilities.None), DateTimeOffset.UtcNow);
        return (connection, requests);
    }
}
