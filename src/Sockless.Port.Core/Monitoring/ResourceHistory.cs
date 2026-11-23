using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Engines;
using Sockless.Port.Settings;

namespace Sockless.Port.Monitoring;

/// <summary>The periods trends can show, each read at a resolution that gives a chart of 60 to 180 points.</summary>
public enum HistoryRange
{
    Hour,
    Day,
    Week,
    Month,
    Quarter,
}

/// <summary>One point of a trend: averages and peaks over <see cref="HistoryView.Bucket"/>. Null where nothing was recorded (the endpoint was offline, or recording was off).</summary>
public sealed record HistoryPoint(DateTimeOffset Time, double? Cpu, double? CpuPeak, long? Memory, long? MemoryPeak,
    double? NetworkRx, double? NetworkTx, double? BlockRead, double? BlockWrite);

/// <summary>What one container used over the whole period: averages, peaks, and the bytes moved in it.</summary>
public sealed record HistoryContainer(string Name, string? Stack, double Cpu, double CpuPeak, long Memory, long MemoryPeak,
    long NetworkRx, long NetworkTx, long BlockRead, long BlockWrite)
{
    public long Network => NetworkRx + NetworkTx;

    public long Disk => BlockRead + BlockWrite;
}

/// <param name="Cpus">The host's, as last recorded; 0 when nothing is.</param>
/// <param name="Containers">Every container seen in the period, most CPU first.</param>
public sealed record HistoryView(HistoryRange Range, DateTimeOffset From, TimeSpan Bucket, int Cpus, long HostMemory,
    IReadOnlyList<HistoryPoint> Points, IReadOnlyList<HistoryContainer> Containers);

/// <summary>
/// Records every online endpoint's resource use for trends, while the settings keep any
/// (<see cref="AppSettings.ResourceHistoryDays"/>). It watches each endpoint through
/// <see cref="ResourceMonitor"/>, as an open page does, and writes a row per container and
/// one of totals each minute. Every few minutes, finished hours are rolled up into hourly rows,
/// minute rows older than <see cref="MinuteRetention"/> are deleted, and hourly ones past the setting.
/// </summary>
public sealed partial class ResourceHistory(ResourceMonitor monitor, EngineConnections connections, PortSettings settings,
    IServiceScopeFactory scopes, TimeProvider time, ILogger<ResourceHistory> logger) : BackgroundService
{
    public const int MinuteSeconds = 60;
    public const int HourSeconds = 3600;

    /// <summary>Minute rows cover the day range with an hour to spare; older ones live on in hourly rows.</summary>
    internal static readonly TimeSpan MinuteRetention = TimeSpan.FromHours(25);

    internal static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>An hour is rolled up only this long after it ends, so its last minute has been written.</summary>
    internal static readonly TimeSpan RollupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Most containers a trend's table lists.</summary>
    public const int MaxContainers = 200;

    private readonly Dictionary<int, Recorder> _recorders = [];

    private sealed record Recorder(EngineConnection Connection, CancellationTokenSource Cancel, Task Task);

    /// <summary>Whether anything is being recorded now.</summary>
    public bool Enabled => settings.Current.ResourceHistoryDays > 0;

    /// <summary>The ranges the setting keeps enough history for.</summary>
    public IReadOnlyList<HistoryRange> Ranges
    {
        get
        {
            var days = settings.Current.ResourceHistoryDays;
            return days <= 0 ? [] : [.. Enum.GetValues<HistoryRange>().Where(r => Spec(r).Span <= TimeSpan.FromDays(Math.Max(1, days)))];
        }
    }

    /// <summary>A range's length, the rows it reads, and how much each point covers.</summary>
    internal static (TimeSpan Span, int Source, TimeSpan Bucket) Spec(HistoryRange range) => range switch
    {
        HistoryRange.Hour => (TimeSpan.FromHours(1), MinuteSeconds, TimeSpan.FromMinutes(1)),
        HistoryRange.Day => (TimeSpan.FromDays(1), MinuteSeconds, TimeSpan.FromMinutes(10)),
        HistoryRange.Week => (TimeSpan.FromDays(7), HourSeconds, TimeSpan.FromHours(1)),
        HistoryRange.Month => (TimeSpan.FromDays(30), HourSeconds, TimeSpan.FromHours(4)),
        HistoryRange.Quarter => (TimeSpan.FromDays(90), HourSeconds, TimeSpan.FromHours(12)),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    // ---------------------------------------------------------------- recording

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var nextMaintenance = time.GetUtcNow() + TimeSpan.FromMinutes(1);
            using var timer = new PeriodicTimer(SyncInterval, time);
            do
            {
                SyncRecorders(stoppingToken);
                var now = time.GetUtcNow();
                if (now >= nextMaintenance)
                {
                    nextMaintenance = now + MaintenanceInterval;
                    try
                    {
                        await MaintainAsync(now, stoppingToken);
                    }
                    catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        LogMaintenanceFailed(logger, ex);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Each recorder writes its unfinished minute as it stops.
            foreach (var recorder in _recorders.Values)
                recorder.Cancel.Cancel();
            await Task.WhenAll(_recorders.Values.Select(r => r.Task));
            _recorders.Clear();
        }
    }

    /// <summary>Starts a recorder per endpoint while recording is on, and stops those of endpoints that are gone (or all, once it's off).</summary>
    internal void SyncRecorders(CancellationToken stoppingToken)
    {
        var wanted = Enabled ? connections.All.ToDictionary(c => c.Id) : [];
        foreach (var (id, recorder) in _recorders.ToList())
        {
            if (wanted.GetValueOrDefault(id) != recorder.Connection)
            {
                recorder.Cancel.Cancel();
                _recorders.Remove(id);
            }
        }
        foreach (var (id, connection) in wanted)
        {
            if (_recorders.ContainsKey(id))
                continue;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _recorders[id] = new Recorder(connection, cancel, Task.Run(() => RecordAsync(connection, cancel.Token)));
        }
    }

    private async Task RecordAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        MinuteBucket? bucket = null;
        try
        {
            await foreach (var snapshot in monitor.WatchAsync(connection, cancellationToken))
            {
                // Offline or unreadable: nothing to record, and the gap shows in the trend.
                if (snapshot.Error is not null)
                    continue;
                var start = snapshot.Time.ToUnixTimeSeconds() / MinuteSeconds * MinuteSeconds;
                if (bucket is not null && bucket.Start != start)
                {
                    if (!bucket.IsEmpty)
                        await WriteAsync(connection.Id, bucket, cancellationToken);
                    bucket = null;
                }
                (bucket ??= new MinuteBucket(start)).Add(snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The monitor is gone: the server is stopping.
        }
        finally
        {
            if (bucket is { IsEmpty: false })
                await WriteAsync(connection.Id, bucket, CancellationToken.None);
        }
    }

    private async Task WriteAsync(int endpointId, MinuteBucket bucket, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            db.ResourceTotals.Add(bucket.Totals(endpointId));
            db.ResourceUsage.AddRange(bucket.Containers(endpointId));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // E.g. the endpoint was removed a moment ago (its rows cascade away anyway).
            LogWriteFailed(logger, endpointId, ex);
        }
    }

    /// <summary>One minute of snapshots of one endpoint, summed until it's written.</summary>
    internal sealed class MinuteBucket(long start)
    {
        private readonly Figures _totals = new();
        private readonly Dictionary<string, (string? Stack, Figures Figures)> _containers = new(StringComparer.Ordinal);
        private int _cpus;
        private long _hostMemory;
        private int _measured;

        public long Start { get; } = start;

        /// <summary>No snapshot with a reading: nothing worth a row.</summary>
        public bool IsEmpty => _totals.IsEmpty;

        public void Add(UsageSnapshot snapshot)
        {
            var measured = snapshot.Containers.Where(c => c.Measured).ToList();
            // Streams that haven't read yet (the sampler just started) would record a false zero.
            if (measured.Count == 0 && snapshot.Containers.Count > 0)
                return;
            (_cpus, _hostMemory) = (snapshot.Cpus, snapshot.HostMemory);
            _measured = Math.Max(_measured, measured.Count);
            _totals.Add(1, measured.Sum(c => c.Cpu), measured.Sum(c => c.Cpu), measured.Sum(c => c.Memory), measured.Sum(c => c.Memory),
                measured.Sum(c => c.NetworkRx), measured.Sum(c => c.NetworkTx), measured.Sum(c => c.BlockRead), measured.Sum(c => c.BlockWrite));
            foreach (var c in measured)
            {
                if (!_containers.TryGetValue(c.Name, out var entry))
                    _containers[c.Name] = entry = (c.Stack, new Figures());
                entry.Figures.Add(1, c.Cpu, c.Cpu, c.Memory, c.Memory, c.NetworkRx, c.NetworkTx, c.BlockRead, c.BlockWrite);
            }
        }

        public ResourceTotalEntry Totals(int endpointId)
        {
            var row = new ResourceTotalEntry { EndpointId = endpointId, Time = Start, Seconds = MinuteSeconds, Cpus = _cpus, HostMemory = _hostMemory, Containers = _measured };
            _totals.CopyTo(row);
            return row;
        }

        public IEnumerable<ResourceUsageEntry> Containers(int endpointId) =>
            _containers.Select(c =>
            {
                var row = new ResourceUsageEntry { EndpointId = endpointId, Time = Start, Seconds = MinuteSeconds, Name = c.Key, Stack = c.Value.Stack };
                c.Value.Figures.CopyTo(row);
                return row;
            });
    }

    /// <summary>Sums of averages weighted by samples, and peaks: how snapshots, minutes and hours combine.</summary>
    internal sealed class Figures
    {
        private int _samples;
        private double _cpu, _cpuPeak, _memory, _rx, _tx, _read, _write;
        private long _memoryPeak;

        public bool IsEmpty => _samples == 0;

        public void Add(int samples, double cpu, double cpuPeak, double memory, long memoryPeak, double rx, double tx, double read, double write)
        {
            if (samples <= 0)
                return;
            _samples += samples;
            _cpu += cpu * samples;
            _memory += memory * samples;
            _rx += rx * samples;
            _tx += tx * samples;
            _read += read * samples;
            _write += write * samples;
            _cpuPeak = Math.Max(_cpuPeak, cpuPeak);
            _memoryPeak = Math.Max(_memoryPeak, memoryPeak);
        }

        public void Add(ResourceTotalEntry r) => Add(r.Samples, r.Cpu, r.CpuPeak, r.Memory, r.MemoryPeak, r.NetworkRx, r.NetworkTx, r.BlockRead, r.BlockWrite);

        public void Add(ResourceUsageEntry r) => Add(r.Samples, r.Cpu, r.CpuPeak, r.Memory, r.MemoryPeak, r.NetworkRx, r.NetworkTx, r.BlockRead, r.BlockWrite);

        public void CopyTo(ResourceTotalEntry row) =>
            (row.Samples, row.Cpu, row.CpuPeak, row.Memory, row.MemoryPeak, row.NetworkRx, row.NetworkTx, row.BlockRead, row.BlockWrite) =
            (_samples, Avg(_cpu), _cpuPeak, (long)Avg(_memory), _memoryPeak, Avg(_rx), Avg(_tx), Avg(_read), Avg(_write));

        public void CopyTo(ResourceUsageEntry row) =>
            (row.Samples, row.Cpu, row.CpuPeak, row.Memory, row.MemoryPeak, row.NetworkRx, row.NetworkTx, row.BlockRead, row.BlockWrite) =
            (_samples, Avg(_cpu), _cpuPeak, (long)Avg(_memory), _memoryPeak, Avg(_rx), Avg(_tx), Avg(_read), Avg(_write));

        public HistoryPoint Point(DateTimeOffset time) => IsEmpty
            ? new HistoryPoint(time, null, null, null, null, null, null, null, null)
            : new HistoryPoint(time, Avg(_cpu), _cpuPeak, (long)Avg(_memory), _memoryPeak, Avg(_rx), Avg(_tx), Avg(_read), Avg(_write));

        private double Avg(double sum) => _samples == 0 ? 0 : sum / _samples;
    }

    // ---------------------------------------------------------------- rollup and pruning

    /// <summary>Rolls finished hours up into hourly rows and deletes what's past its retention.</summary>
    internal async Task MaintainAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var days = settings.Current.ResourceHistoryDays;
        if (days <= 0)
        {
            await db.ResourceUsage.ExecuteDeleteAsync(cancellationToken);
            await db.ResourceTotals.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var until = (now - RollupDelay).ToUnixTimeSeconds() / HourSeconds * HourSeconds;
        var endpoints = await db.ResourceTotals.Where(t => t.Seconds == MinuteSeconds && t.Time < until)
            .Select(t => t.EndpointId).Distinct().ToListAsync(cancellationToken);
        foreach (var endpointId in endpoints)
        {
            // From the hour after the last one rolled up, so a restart neither skips nor repeats one.
            var last = await db.ResourceTotals.Where(t => t.EndpointId == endpointId && t.Seconds == HourSeconds)
                .MaxAsync(t => (long?)t.Time, cancellationToken);
            var from = last + HourSeconds ?? 0;
            var totals = await db.ResourceTotals.AsNoTracking()
                .Where(t => t.EndpointId == endpointId && t.Seconds == MinuteSeconds && t.Time >= from && t.Time < until)
                .ToListAsync(cancellationToken);
            var usage = await db.ResourceUsage.AsNoTracking()
                .Where(u => u.EndpointId == endpointId && u.Seconds == MinuteSeconds && u.Time >= from && u.Time < until)
                .ToListAsync(cancellationToken);
            db.ResourceTotals.AddRange(RollUpTotals(endpointId, totals));
            db.ResourceUsage.AddRange(RollUpUsage(endpointId, usage));
            await db.SaveChangesAsync(cancellationToken);
        }

        var minuteCutoff = (now - MinuteRetention).ToUnixTimeSeconds();
        var hourCutoff = (now - TimeSpan.FromDays(days)).ToUnixTimeSeconds();
        await db.ResourceUsage.Where(u => (u.Seconds == MinuteSeconds && u.Time < minuteCutoff) || (u.Seconds != MinuteSeconds && u.Time < hourCutoff)).ExecuteDeleteAsync(cancellationToken);
        await db.ResourceTotals.Where(t => (t.Seconds == MinuteSeconds && t.Time < minuteCutoff) || (t.Seconds != MinuteSeconds && t.Time < hourCutoff)).ExecuteDeleteAsync(cancellationToken);
    }

    internal static IEnumerable<ResourceTotalEntry> RollUpTotals(int endpointId, IEnumerable<ResourceTotalEntry> minutes) =>
        minutes.GroupBy(m => m.Time / HourSeconds * HourSeconds).Select(hour =>
        {
            var figures = new Figures();
            foreach (var m in hour)
                figures.Add(m);
            var latest = hour.MaxBy(m => m.Time)!;
            var row = new ResourceTotalEntry
            {
                EndpointId = endpointId, Time = hour.Key, Seconds = HourSeconds,
                Cpus = latest.Cpus, HostMemory = latest.HostMemory, Containers = hour.Max(m => m.Containers),
            };
            figures.CopyTo(row);
            return row;
        });

    internal static IEnumerable<ResourceUsageEntry> RollUpUsage(int endpointId, IEnumerable<ResourceUsageEntry> minutes) =>
        minutes.GroupBy(m => (Hour: m.Time / HourSeconds * HourSeconds, m.Name)).Select(hour =>
        {
            var figures = new Figures();
            foreach (var m in hour)
                figures.Add(m);
            var row = new ResourceUsageEntry
            {
                EndpointId = endpointId, Time = hour.Key.Hour, Seconds = HourSeconds, Name = hour.Key.Name,
                Stack = hour.MaxBy(m => m.Time)!.Stack,
            };
            figures.CopyTo(row);
            return row;
        });

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The endpoint's totals, or one container's use when <paramref name="name"/> is given, over
    /// <paramref name="range"/> up to now, and what every container used in it.
    /// </summary>
    public async Task<HistoryView> ReadAsync(int endpointId, HistoryRange range, string? name, CancellationToken cancellationToken)
    {
        var (span, source, bucket) = Spec(range);
        var now = time.GetUtcNow();
        var bucketSeconds = (long)bucket.TotalSeconds;
        var to = now.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds + bucketSeconds;
        var from = to - (long)span.TotalSeconds;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();

        // Hourly ranges add the minutes since the last rolled-up hour, so they reach now.
        long? tail = null;
        if (source == HourSeconds)
        {
            var lastHour = await db.ResourceTotals.Where(t => t.EndpointId == endpointId && t.Seconds == HourSeconds)
                .MaxAsync(t => (long?)t.Time, cancellationToken);
            tail = Math.Max(from, lastHour + HourSeconds ?? from);
        }

        var totals = await db.ResourceTotals.AsNoTracking()
            .Where(t => t.EndpointId == endpointId && t.Time >= from
                        && (t.Seconds == source || (tail != null && t.Seconds == MinuteSeconds && t.Time >= tail)))
            .ToListAsync(cancellationToken);
        var latest = totals.MaxBy(t => t.Time);

        var usage = db.ResourceUsage.AsNoTracking()
            .Where(u => u.EndpointId == endpointId && u.Time >= from
                        && (u.Seconds == source || (tail != null && u.Seconds == MinuteSeconds && u.Time >= tail)));

        List<HistoryPoint> points;
        if (string.IsNullOrEmpty(name))
        {
            points = Points(totals.Select(t => (t.Time, (Action<Figures>)(f => f.Add(t)))), from, to, bucketSeconds);
        }
        else
        {
            var rows = await usage.Where(u => u.Name == name).ToListAsync(cancellationToken);
            points = Points(rows.Select(u => (u.Time, (Action<Figures>)(f => f.Add(u)))), from, to, bucketSeconds);
        }

        // Averages weighted by samples; bytes moved from the average rate over the time sampled.
        var seconds = ResourceMonitor.PublishInterval.TotalSeconds;
        var grouped = await usage
            .GroupBy(u => new { u.Name, u.Stack })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Stack,
                Samples = g.Sum(u => u.Samples),
                Cpu = g.Sum(u => u.Cpu * u.Samples),
                CpuPeak = g.Max(u => u.CpuPeak),
                Memory = g.Sum(u => (double)u.Memory * u.Samples),
                MemoryPeak = g.Max(u => u.MemoryPeak),
                NetworkRx = g.Sum(u => u.NetworkRx * u.Samples),
                NetworkTx = g.Sum(u => u.NetworkTx * u.Samples),
                BlockRead = g.Sum(u => u.BlockRead * u.Samples),
                BlockWrite = g.Sum(u => u.BlockWrite * u.Samples),
                Last = g.Max(u => u.Time),
            })
            .ToListAsync(cancellationToken);

        // A container that moved between stacks is one row, under the stack it was last in.
        var containers = grouped
            .GroupBy(g => g.Name, StringComparer.Ordinal)
            .Select(n =>
            {
                var samples = n.Sum(g => g.Samples);
                return new HistoryContainer(n.Key, n.MaxBy(g => g.Last)!.Stack,
                    samples == 0 ? 0 : n.Sum(g => g.Cpu) / samples, n.Max(g => g.CpuPeak),
                    samples == 0 ? 0 : (long)(n.Sum(g => g.Memory) / samples), n.Max(g => g.MemoryPeak),
                    (long)(n.Sum(g => g.NetworkRx) * seconds), (long)(n.Sum(g => g.NetworkTx) * seconds),
                    (long)(n.Sum(g => g.BlockRead) * seconds), (long)(n.Sum(g => g.BlockWrite) * seconds));
            })
            .OrderByDescending(c => c.Cpu).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(MaxContainers)
            .ToList();

        return new HistoryView(range, DateTimeOffset.FromUnixTimeSeconds(from), bucket, latest?.Cpus ?? 0, latest?.HostMemory ?? 0, points, containers);
    }

    /// <summary>One point per bucket from <paramref name="from"/> to <paramref name="to"/>, empty where no row falls.</summary>
    internal static List<HistoryPoint> Points(IEnumerable<(long Time, Action<Figures> Add)> rows, long from, long to, long bucketSeconds)
    {
        var buckets = new Dictionary<long, Figures>();
        foreach (var (rowTime, add) in rows)
        {
            var key = (rowTime - from) / bucketSeconds;
            if (!buckets.TryGetValue(key, out var figures))
                buckets[key] = figures = new Figures();
            add(figures);
        }
        var count = (to - from) / bucketSeconds;
        return [.. Enumerable.Range(0, (int)count).Select(i =>
            buckets.GetValueOrDefault(i, new Figures()).Point(DateTimeOffset.FromUnixTimeSeconds(from + i * bucketSeconds)))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write resource history of endpoint {Endpoint}")]
    private static partial void LogWriteFailed(ILogger logger, int endpoint, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rolling up or pruning resource history failed")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);
}
