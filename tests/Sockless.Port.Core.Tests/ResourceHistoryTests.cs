using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;
using Sockless.Port.Settings;
using static Sockless.Port.Core.Tests.ResourceMonitorTests;

namespace Sockless.Port.Core.Tests;

public sealed class ResourceHistoryTests : IAsyncLifetime
{
    private const int Endpoint = 1;
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 30, 0, TimeSpan.Zero);

    // One open in-memory connection shared by every scope, so the database outlives each DbContext.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Endpoints.Add(new EngineEndpoint { Id = Endpoint, Name = "local", Address = "unix:///fake.sock", Kind = EndpointKind.Local });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private (ResourceHistory History, PortSettings Settings, ResourceMonitor Monitor) Create(TimeProvider time, EngineConnections? connections = null)
    {
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var settings = new PortSettings(scopes);
        var monitor = new ResourceMonitor(time, NullLogger<ResourceMonitor>.Instance);
        return (new ResourceHistory(monitor, connections ?? new EngineConnections(), settings, scopes, time, NullLogger<ResourceHistory>.Instance), settings, monitor);
    }

    private async Task SeedAsync(params object[] rows)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        db.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<T> ReadDbAsync<T>(Func<PortDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<PortDbContext>());
    }

    private static long At(DateTimeOffset time) => time.ToUnixTimeSeconds();

    private static ResourceTotalEntry Total(DateTimeOffset time, int seconds, int samples, double cpu, double cpuPeak, long memory = 0) => new()
    {
        EndpointId = Endpoint, Time = At(time), Seconds = seconds, Samples = samples, Cpu = cpu, CpuPeak = cpuPeak,
        Memory = memory, MemoryPeak = memory, Cpus = 4, HostMemory = HostMemory, Containers = 2,
    };

    private static ResourceUsageEntry Usage(DateTimeOffset time, int seconds, string name, int samples, double cpu, double rx = 0, string? stack = null) => new()
    {
        EndpointId = Endpoint, Time = At(time), Seconds = seconds, Name = name, Stack = stack, Samples = samples,
        Cpu = cpu, CpuPeak = cpu, Memory = 1_000, MemoryPeak = 1_000, NetworkRx = rx,
    };

    // ---------------------------------------------------------------- minutes

    [Fact]
    public void A_minute_averages_measured_containers_and_keeps_their_peaks()
    {
        var bucket = new ResourceHistory.MinuteBucket(At(Now));
        bucket.Add(new UsageSnapshot(Now, 4, HostMemory, 2,
        [
            new ContainerUsage("a", "web", "shop", true, 10, 100, 0, 1_000, 0, 0, 0, 1),
            new ContainerUsage("b", "db", null, false, 0, 0, 0, 0, 0, 0, 0, 0), // no reading yet
        ]));
        bucket.Add(new UsageSnapshot(Now.AddSeconds(2), 4, HostMemory, 2,
        [
            new ContainerUsage("a", "web", "shop", true, 30, 300, 0, 3_000, 0, 0, 0, 1),
            new ContainerUsage("b", "db", null, true, 50, 500, 0, 0, 0, 0, 0, 1),
        ]));

        var totals = bucket.Totals(Endpoint);
        var usage = bucket.Containers(Endpoint).ToDictionary(u => u.Name);

        Assert.Equal(2, totals.Samples);
        Assert.Equal(45, totals.Cpu);      // (10 + 80) / 2
        Assert.Equal(80, totals.CpuPeak);  // the total's peak, not a container's
        Assert.Equal(2, totals.Containers);
        Assert.Equal(20, usage["web"].Cpu);
        Assert.Equal(30, usage["web"].CpuPeak);
        Assert.Equal(2_000, usage["web"].NetworkRx);
        Assert.Equal("shop", usage["web"].Stack);
        Assert.Equal(1, usage["db"].Samples); // only its measured reading
    }

    [Fact]
    public void A_snapshot_before_any_reading_is_not_recorded()
    {
        var bucket = new ResourceHistory.MinuteBucket(At(Now));
        bucket.Add(new UsageSnapshot(Now, 4, HostMemory, 1, [new ContainerUsage("a", "web", null, false, 0, 0, 0, 0, 0, 0, 0, 0)]));

        Assert.Equal(0, bucket.Totals(Endpoint).Samples);
        Assert.Empty(bucket.Containers(Endpoint));
    }

    // ---------------------------------------------------------------- rollup and pruning

    [Fact]
    public async Task Finished_hours_roll_up_once_weighted_by_samples()
    {
        var ct = TestContext.Current.CancellationToken;
        var ten = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            Total(ten, 60, 30, cpu: 10, cpuPeak: 20),
            Total(ten.AddMinutes(1), 60, 10, cpu: 50, cpuPeak: 90),
            Total(ten.AddHours(1).AddMinutes(59), 60, 30, cpu: 5, cpuPeak: 5),
            Total(ten.AddHours(2).AddMinutes(10), 60, 30, cpu: 7, cpuPeak: 7), // the current hour: not yet
            Usage(ten, 60, "web", 30, 10, stack: "old"),
            Usage(ten.AddMinutes(1), 60, "web", 10, 50, stack: "shop"));
        var (history, _, monitor) = Create(new FixedTime(Now));
        using var _ = monitor;

        await history.MaintainAsync(Now, ct);
        await history.MaintainAsync(Now, ct);

        var hours = await ReadDbAsync(db => db.ResourceTotals.Where(t => t.Seconds == 3600).OrderBy(t => t.Time).ToListAsync(ct));
        Assert.Collection(hours,
            h => { Assert.Equal(At(ten), h.Time); Assert.Equal(40, h.Samples); Assert.Equal(20, h.Cpu); Assert.Equal(90, h.CpuPeak); },
            h => Assert.Equal(At(ten.AddHours(1)), h.Time));
        var web = await ReadDbAsync(db => db.ResourceUsage.SingleAsync(u => u.Seconds == 3600, ct));
        Assert.Equal(20, web.Cpu);
        Assert.Equal("shop", web.Stack); // the stack it was last in
    }

    [Fact]
    public async Task Old_rows_are_deleted_and_turning_history_off_deletes_all()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(
            Total(Now.AddHours(-26), 60, 1, 1, 1),  // past the minute retention
            Total(Now.AddMinutes(-5), 60, 1, 1, 1),
            Total(Now.AddDays(-8), 3600, 1, 1, 1),  // past the 7 days kept by default
            Total(Now.AddDays(-6), 3600, 1, 1, 1));
        var (history, settings, monitor) = Create(new FixedTime(Now));
        using var _ = monitor;

        await history.MaintainAsync(Now, ct);

        var times = await ReadDbAsync(db => db.ResourceTotals.Select(t => new { t.Time, t.Seconds }).ToListAsync(ct));
        Assert.Contains(times, t => t.Seconds == 60 && t.Time == At(Now.AddMinutes(-5)));
        Assert.Contains(times, t => t.Seconds == 3600 && t.Time == At(Now.AddDays(-6)));
        Assert.DoesNotContain(times, t => t.Seconds == 60 && t.Time == At(Now.AddHours(-26)));
        Assert.DoesNotContain(times, t => t.Time == At(Now.AddDays(-8)));

        await settings.SaveAsync(AppSettings.Default with { ResourceHistoryDays = 0 }, ct);
        await history.MaintainAsync(Now, ct);

        Assert.Equal(0, await ReadDbAsync(db => db.ResourceTotals.CountAsync(ct)));
        Assert.Empty(history.Ranges);
        Assert.False(history.Enabled);
    }

    [Fact]
    public async Task Ranges_follow_how_long_history_is_kept()
    {
        var (history, settings, monitor) = Create(new FixedTime(Now));
        using var _ = monitor;

        Assert.Equal([HistoryRange.Hour, HistoryRange.Day, HistoryRange.Week], history.Ranges);
        await settings.SaveAsync(AppSettings.Default with { ResourceHistoryDays = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal([HistoryRange.Hour, HistoryRange.Day], history.Ranges);
        await settings.SaveAsync(AppSettings.Default with { ResourceHistoryDays = 90 }, TestContext.Current.CancellationToken);
        Assert.Equal(Enum.GetValues<HistoryRange>(), history.Ranges);
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task The_last_hour_is_by_the_minute_with_gaps_and_a_table_of_containers()
    {
        var ct = TestContext.Current.CancellationToken;
        var noon = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            Total(noon, 60, 30, 40, 60, memory: 5_000),
            Total(noon.AddMinutes(2), 60, 30, 20, 30),
            Usage(noon, 60, "web", 30, cpu: 30, rx: 100, stack: "shop"),
            Usage(noon.AddMinutes(2), 60, "web", 30, cpu: 10, rx: 100, stack: "shop"),
            Usage(noon, 60, "db", 30, cpu: 5));
        var (history, _, monitor) = Create(new FixedTime(Now));
        using var _ = monitor;

        var view = await history.ReadAsync(Endpoint, HistoryRange.Hour, name: null, ct);

        Assert.Equal(60, view.Points.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), view.Bucket);
        Assert.Equal(4, view.Cpus);
        var at = view.Points.ToDictionary(p => p.Time);
        Assert.Equal(40, at[noon].Cpu);
        Assert.Equal(60, at[noon].CpuPeak);
        Assert.Equal(5_000, at[noon].Memory);
        Assert.Null(at[noon.AddMinutes(1)].Cpu); // nothing recorded: a gap
        Assert.Equal(20, at[noon.AddMinutes(2)].Cpu);

        Assert.Collection(view.Containers,
            web =>
            {
                Assert.Equal("web", web.Name);
                Assert.Equal("shop", web.Stack);
                Assert.Equal(20, web.Cpu);
                Assert.Equal(30, web.CpuPeak);
                Assert.Equal(100 * 60 * 2, web.NetworkRx); // 100 B/s over 60 samples two seconds apart
            },
            db => Assert.Equal("db", db.Name));

        var one = await history.ReadAsync(Endpoint, HistoryRange.Hour, name: "web", ct);
        Assert.Equal(30, one.Points.Single(p => p.Time == noon).Cpu);
    }

    [Fact]
    public async Task Hourly_ranges_reach_now_with_the_minutes_not_yet_rolled_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var ten = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            Total(ten, 3600, 1800, 10, 10),
            Total(ten.AddMinutes(30), 60, 30, 999, 999), // already in the 10:00 hour
            Total(ten.AddHours(2), 60, 30, 40, 40));     // after the last hour rolled up
        var (history, _, monitor) = Create(new FixedTime(Now));
        using var _ = monitor;

        var view = await history.ReadAsync(Endpoint, HistoryRange.Week, name: null, ct);

        Assert.Equal(7 * 24, view.Points.Count);
        Assert.Equal(10, view.Points.Single(p => p.Time == ten).Cpu);
        Assert.Equal(40, view.Points.Single(p => p.Time == ten.AddHours(2)).Cpu);
        Assert.Null(view.Points.Single(p => p.Time == ten.AddHours(1)).Cpu);
    }

    // ---------------------------------------------------------------- recording

    [Fact]
    public async Task Recording_writes_a_row_per_minute_for_online_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeEngine(request => request.RequestUri!.AbsolutePath switch
        {
            "/version" => Json("""{"ApiVersion":"1.51"}"""),
            "/v1.51/info" => Json($$"""{"NCPU":4,"MemTotal":{{HostMemory}}}"""),
            "/v1.51/containers/json" => Json("""[{"Id":"aaa","Names":["/web"],"State":"running","Created":1,"Labels":{"com.docker.compose.project":"shop"}}]"""),
            "/v1.51/containers/aaa/stats" => Lines(StatsLine("2026-11-21T20:41:16Z", cpu: 100_000_000, rx: 1_000),
                                                   StatsLine("2026-11-21T20:41:17Z", cpu: 200_000_000, rx: 3_000)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = await DockerClient.ConnectAsync(handler, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, ct);
        var connection = new EngineConnection(Endpoint, "local", new UnixSocketAddress("/fake.sock"), EndpointKind.Local);
        connection.SetOnline(client, new EngineDescriptor(EngineKind.Docker, "29.0.0", new Version(1, 51), "linux", "amd64", EngineCapabilities.None), DateTimeOffset.UtcNow);
        var connections = new EngineConnections();
        connections.Add(connection);
        var time = new ShiftedTime();
        var (history, _, monitor) = Create(time, connections);
        using var _ = monitor;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        history.SyncRecorders(stop.Token);
        // Wait for measured snapshots, then move into the next minute so the first one is written.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !(await ReadDbAsync(db => db.ResourceUsage.AnyAsync(ct))))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            time.Shift(TimeSpan.FromSeconds(30));
        }
        stop.Cancel();

        var web = await ReadDbAsync(db => db.ResourceUsage.FirstAsync(ct));
        Assert.Equal("web", web.Name);
        Assert.Equal("shop", web.Stack);
        Assert.Equal(60, web.Seconds);
        Assert.True(web.Samples > 0);
        Assert.True(await ReadDbAsync(db => db.ResourceTotals.AnyAsync(t => t.EndpointId == Endpoint && t.Cpus == 4, ct)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
