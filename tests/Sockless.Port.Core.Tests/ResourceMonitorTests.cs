using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Streams;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;

namespace Sockless.Port.Core.Tests;

public sealed class ResourceMonitorTests
{
    internal const long HostMemory = 8_000_000_000;

    private static StatsSample Sample(double seconds, long rx = 0, long tx = 0, long read = 0, long write = 0, long limit = HostMemory) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), 12.5, 100_000_000, limit, rx, tx, read, write, 4);

    [Fact]
    public void Rates_come_from_the_last_two_readings()
    {
        var usage = ResourceMonitor.Measure("id", "web", "shop", Sample(12, rx: 3_000, tx: 1_500, read: 8_000, write: 0),
            Sample(10, rx: 1_000, tx: 500, read: 0, write: 0), HostMemory, error: null);

        Assert.True(usage.Measured);
        Assert.Equal(1_000, usage.NetworkRx);
        Assert.Equal(500, usage.NetworkTx);
        Assert.Equal(4_000, usage.BlockRead);
        Assert.Equal(0, usage.BlockWrite);
        Assert.Equal(12.5, usage.Cpu);
        Assert.Equal(4, usage.Pids);
    }

    [Fact]
    public void A_counter_that_went_backwards_reads_as_zero_not_negative()
    {
        // The container restarted between readings, so its counters started again.
        var usage = ResourceMonitor.Measure("id", "web", null, Sample(12, rx: 10), Sample(10, rx: 5_000), HostMemory, null);

        Assert.Equal(0, usage.NetworkRx);
    }

    [Fact]
    public void One_reading_has_no_rates_yet()
    {
        var usage = ResourceMonitor.Measure("id", "web", null, Sample(10, rx: 5_000), previous: null, HostMemory, null);

        Assert.True(usage.Measured);
        Assert.Equal(0, usage.NetworkRx);
    }

    [Fact]
    public void No_reading_is_not_measured()
    {
        var usage = ResourceMonitor.Measure("id", "web", null, latest: null, previous: null, HostMemory, "no cgroup");

        Assert.False(usage.Measured);
        Assert.Equal("no cgroup", usage.Error);
    }

    [Theory]
    [InlineData(HostMemory, 0)]            // no limit: the engine reports the host's memory
    [InlineData(HostMemory + 1024, 0)]     // or a little more on some kernels
    [InlineData(536_870_912, 536_870_912)] // a real limit
    public void Only_a_real_memory_limit_is_shown(long reported, long shown)
    {
        var usage = ResourceMonitor.Measure("id", "web", null, Sample(10, limit: reported), null, HostMemory, null);

        Assert.Equal(shown, usage.MemoryLimit);
    }

    [Fact]
    public void Snapshot_totals_add_up_the_containers()
    {
        var snapshot = new UsageSnapshot(DateTimeOffset.UnixEpoch, 4, HostMemory, 2,
        [
            new ContainerUsage("a", "a", null, true, 50, 100, 0, 10, 20, 30, 40, 1),
            new ContainerUsage("b", "b", null, true, 25, 200, 0, 1, 2, 3, 4, 1),
        ]);

        Assert.Equal(75, snapshot.Cpu);
        Assert.Equal(300, snapshot.Memory);
        Assert.Equal(11, snapshot.NetworkRx);
        Assert.Equal(22, snapshot.NetworkTx);
        Assert.Equal(33, snapshot.BlockRead);
        Assert.Equal(44, snapshot.BlockWrite);
    }

    [Fact]
    public async Task Measures_running_containers_and_shares_one_sampler_between_viewers()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<string>();
        var handler = new FakeEngine(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Enqueue($"{path}{request.RequestUri.Query}");
            return path switch
            {
                "/version" => Json("""{"ApiVersion":"1.51"}"""),
                "/v1.51/info" => Json($$"""{"NCPU":4,"MemTotal":{{HostMemory}}}"""),
                "/v1.51/containers/json" => Json("""
                    [
                      {"Id":"aaa","Names":["/web"],"State":"running","Created":1,"Labels":{"com.docker.compose.project":"shop"}},
                      {"Id":"bbb","Names":["/files"],"State":"running","Created":2,"Labels":{"sockless-port.helper":"volume-browser"}}
                    ]
                    """),
                "/v1.51/containers/aaa/stats" => Lines(StatsLine("2026-11-21T20:41:16Z", cpu: 100_000_000, rx: 1_000),
                                                       StatsLine("2026-11-21T20:41:17Z", cpu: 200_000_000, rx: 3_000)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") },
            };
        });
        using var client = await DockerClient.ConnectAsync(handler, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, ct);
        var connection = new EngineConnection(3, "fake", new UnixSocketAddress("/fake.sock"), EndpointKind.Local);
        connection.SetOnline(client, new EngineDescriptor(EngineKind.Docker, "29.0.0", new Version(1, 51), "linux", "amd64", EngineCapabilities.None), DateTimeOffset.UtcNow);

        var time = new ShiftedTime();
        using var monitor = new ResourceMonitor(time, NullLogger<ResourceMonitor>.Instance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // Two viewers at once.
        var second = monitor.WatchAsync(connection, timeout.Token).GetAsyncEnumerator(timeout.Token);
        UsageSnapshot? measured = null;
        await foreach (var snapshot in monitor.WatchAsync(connection, timeout.Token))
        {
            if (snapshot.Containers is [{ Measured: true, NetworkRx: > 0 }])
            {
                measured = snapshot;
                break;
            }
        }
        Assert.True(await second.MoveNextAsync());
        await second.DisposeAsync();

        Assert.NotNull(measured);
        var web = Assert.Single(measured.Containers); // the helper isn't counted
        Assert.Equal("web", web.Name);
        Assert.Equal("shop", web.Stack);
        Assert.Equal(2_000, web.NetworkRx);
        Assert.Equal(40, web.Cpu, precision: 6); // 0.1 of the system's time × 4 CPUs
        Assert.Equal(0, web.MemoryLimit);        // the reported limit is the host's memory
        Assert.Equal(1, measured.Running);
        Assert.Equal(4, measured.Cpus);
        Assert.Equal(HostMemory, measured.HostMemory);
        Assert.Single(calls, c => c == "/v1.51/info");                         // read once, not per viewer
        Assert.DoesNotContain(calls, c => c.StartsWith("/v1.51/containers/bbb", StringComparison.Ordinal));

        // The overview reads the newest snapshot without watching; another connection with the same id gets none.
        Assert.NotNull(monitor.Latest(connection, TimeSpan.FromMinutes(1)));
        Assert.Null(monitor.Latest(new EngineConnection(3, "other", new UnixSocketAddress("/fake.sock"), EndpointKind.Local), TimeSpan.FromMinutes(1)));

        // With no viewers left, the sampler lingers, then stops.
        Assert.True(monitor.IsSampling(3));
        time.Shift(ResourceMonitor.Linger + TimeSpan.FromSeconds(1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.IsSampling(3) && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        Assert.False(monitor.IsSampling(3));
        Assert.Null(monitor.Latest(connection, TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task An_offline_endpoint_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new EngineConnection(4, "gone", new UnixSocketAddress("/nonexistent.sock"), EndpointKind.Local);
        using var monitor = new ResourceMonitor(TimeProvider.System, NullLogger<ResourceMonitor>.Instance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var snapshots = monitor.WatchAsync(connection, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await snapshots.MoveNextAsync());

        Assert.Equal("The engine is offline.", snapshots.Current.Error);
        Assert.Empty(snapshots.Current.Containers);
    }

    internal static string StatsLine(string read, ulong cpu, long rx) =>
        $$$"""{"read":"{{{read}}}","cpu_stats":{"cpu_usage":{"total_usage":{{{cpu + 100_000_000}}}},"system_cpu_usage":2000000000,"online_cpus":4},"precpu_stats":{"cpu_usage":{"total_usage":{{{cpu}}}},"system_cpu_usage":1000000000,"online_cpus":4},"memory_stats":{"usage":104857600,"limit":{{{HostMemory}}},"stats":{"inactive_file":4857600}},"networks":{"eth0":{"rx_bytes":{{{rx}}},"tx_bytes":0}},"pids_stats":{"current":3}}""";

    internal static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Lines(params string[] lines) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Join('\n', lines) + "\n", Encoding.UTF8, "application/json") };

    internal sealed class FakeEngine(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>The system clock, moved forward on request (timers still run in real time).</summary>
    internal sealed class ShiftedTime : TimeProvider
    {
        private long _offsetTicks;

        public void Shift(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
    }
}
