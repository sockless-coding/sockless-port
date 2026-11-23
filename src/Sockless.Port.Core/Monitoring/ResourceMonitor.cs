using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Streams;
using Sockless.Port.Engines;

namespace Sockless.Port.Monitoring;

/// <summary>
/// One running container's resource use. CPU is the share of the host where 100 is one full CPU,
/// as <c>docker stats</c> shows it; the rates are bytes per second, from the last two readings.
/// </summary>
/// <param name="Measured">False until the first reading arrives; the numbers are then zero.</param>
/// <param name="MemoryLimit">The container's own limit, or 0 when it has none (the engine reports the host's memory then).</param>
/// <param name="Error">Why this container can't be measured, e.g. no cgroups on a rootless engine.</param>
public sealed record ContainerUsage(string Id, string Name, string? Stack, bool Measured, double Cpu, long Memory, long MemoryLimit,
    double NetworkRx, double NetworkTx, double BlockRead, double BlockWrite, long Pids, string? Error = null);

/// <summary>Every running container on an endpoint at one moment, and the host they share.</summary>
/// <param name="Cpus">The host's CPUs, so total CPU use can be read against <c>Cpus × 100</c>.</param>
/// <param name="Running">Running containers on the endpoint; more than <see cref="Containers"/> when there are over <see cref="ResourceMonitor.MaxContainers"/>.</param>
/// <param name="Error">Why the endpoint can't be read now (offline, or listing failed). The containers are then empty.</param>
public sealed record UsageSnapshot(DateTimeOffset Time, int Cpus, long HostMemory, int Running, IReadOnlyList<ContainerUsage> Containers, string? Error = null)
{
    public double Cpu => Containers.Sum(c => c.Cpu);

    public long Memory => Containers.Sum(c => c.Memory);

    public double NetworkRx => Containers.Sum(c => c.NetworkRx);

    public double NetworkTx => Containers.Sum(c => c.NetworkTx);

    public double BlockRead => Containers.Sum(c => c.BlockRead);

    public double BlockWrite => Containers.Sum(c => c.BlockWrite);
}

/// <summary>
/// Live resource use of every running container on an endpoint. The engine has no call that
/// measures all containers, so each one gets its own stats stream, as <c>docker stats</c> does.
/// One sampler per endpoint serves every viewer, starts with the first and stops shortly after
/// the last one leaves.
/// </summary>
public sealed class ResourceMonitor(TimeProvider timeProvider, ILogger<ResourceMonitor> log) : IDisposable
{
    /// <summary>How often viewers get a snapshot.</summary>
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often the running containers are listed again, to start and stop streams.</summary>
    internal static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a sampler keeps its streams after the last viewer leaves, so moving between pages doesn't restart them.</summary>
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(15);

    /// <summary>How long a container whose stats failed waits before it's tried again.</summary>
    internal static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(30);

    /// <summary>Most containers measured at once on one endpoint: each is an open connection to the engine.</summary>
    public const int MaxContainers = 200;

    private readonly TimeProvider _time = timeProvider;
    private readonly ILogger _logger = log;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, EndpointSampler> _samplers = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The latest snapshot at once if there is one, then one every <see cref="PublishInterval"/> until cancelled.</summary>
    public async IAsyncEnumerable<UsageSnapshot> WatchAsync(EngineConnection connection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Only the newest snapshot matters; a slow viewer skips the ones it missed.
        var channel = Channel.CreateBounded<UsageSnapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        EndpointSampler sampler;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            if (!_samplers.TryGetValue(connection.Id, out sampler!) || sampler.Connection != connection)
            {
                sampler = new EndpointSampler(this, connection);
                _samplers[connection.Id] = sampler;
                sampler.Start();
            }
            sampler.Subscribers.Add(channel.Writer);
            if (sampler.Latest is { } latest)
                channel.Writer.TryWrite(latest);
        }

        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                sampler.Subscribers.Remove(channel.Writer);
                if (sampler.Subscribers.Count == 0)
                    sampler.IdleSince = _time.GetUtcNow();
            }
        }
    }

    /// <summary>Endpoints with a running sampler, for tests.</summary>
    internal bool IsSampling(int endpointId)
    {
        lock (_gate)
            return _samplers.ContainsKey(endpointId);
    }

    /// <summary>Stops every sampler. The token source isn't disposed: samplers may still be reading it as they wind down.</summary>
    public void Dispose()
    {
        lock (_gate)
            _stopping.Cancel();
    }

    /// <summary>Called by a sampler between rounds: true when it should stop (removed under the lock, so no viewer joins it meanwhile).</summary>
    private bool TryRetire(EndpointSampler sampler)
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
                return true;
            if (sampler.Subscribers.Count > 0 || _time.GetUtcNow() - sampler.IdleSince < Linger)
                return false;
            if (_samplers.GetValueOrDefault(sampler.Connection.Id) == sampler)
                _samplers.Remove(sampler.Connection.Id);
            return true;
        }
    }

    private void Publish(EndpointSampler sampler, UsageSnapshot snapshot)
    {
        lock (_gate)
        {
            sampler.Latest = snapshot;
            foreach (var subscriber in sampler.Subscribers)
                subscriber.TryWrite(snapshot);
        }
    }

    /// <summary>Rates from two readings of a cumulative counter; zero when it went backwards (a restart) or no time passed.</summary>
    internal static double Rate(long now, long before, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero && now >= before ? (now - before) / elapsed.TotalSeconds : 0;

    /// <summary>A container's row from its last two readings.</summary>
    /// <param name="hostMemory">The engine reports the host's memory as the limit of a container without one, which isn't a limit worth showing.</param>
    internal static ContainerUsage Measure(string id, string name, string? stack, StatsSample? latest, StatsSample? previous, long hostMemory, string? error)
    {
        if (latest is null)
            return new ContainerUsage(id, name, stack, false, 0, 0, 0, 0, 0, 0, 0, 0, error);

        var elapsed = previous is null ? TimeSpan.Zero : latest.Read - previous.Read;
        var limit = latest.MemoryLimit > 0 && (hostMemory <= 0 || latest.MemoryLimit < hostMemory) ? latest.MemoryLimit : 0;
        return new ContainerUsage(id, name, stack, true, latest.CpuPercent, latest.MemoryUsage, limit,
            previous is null ? 0 : Rate(latest.NetworkRx, previous.NetworkRx, elapsed),
            previous is null ? 0 : Rate(latest.NetworkTx, previous.NetworkTx, elapsed),
            previous is null ? 0 : Rate(latest.BlockRead, previous.BlockRead, elapsed),
            previous is null ? 0 : Rate(latest.BlockWrite, previous.BlockWrite, elapsed),
            latest.Pids, error);
    }

    /// <summary>Samples one endpoint: lists its running containers and keeps a stats stream open to each.</summary>
    private sealed class EndpointSampler(ResourceMonitor monitor, EngineConnection connection)
    {
        private readonly Dictionary<string, ContainerStream> _streams = new(StringComparer.Ordinal);
        private DockerClient? _client;
        private int _cpus;
        private long _hostMemory;
        private int _running;
        private string? _error;
        private DateTimeOffset _nextList = DateTimeOffset.MinValue;

        public EngineConnection Connection { get; } = connection;

        /// <summary>Guarded by the monitor's lock.</summary>
        public List<ChannelWriter<UsageSnapshot>> Subscribers { get; } = [];

        /// <summary>Guarded by the monitor's lock.</summary>
        public DateTimeOffset IdleSince { get; set; }

        /// <summary>Guarded by the monitor's lock.</summary>
        public UsageSnapshot? Latest { get; set; }

        public void Start() => _ = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            var stopping = monitor._stopping.Token;
            try
            {
                while (!monitor.TryRetire(this))
                {
                    await RoundAsync(stopping);
                    await Task.Delay(PublishInterval, monitor._time, stopping);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                monitor._logger.LogError(ex, "Resource sampling of endpoint {Endpoint} stopped", Connection.Id);
                lock (monitor._gate)
                {
                    if (monitor._samplers.GetValueOrDefault(Connection.Id) == this)
                        monitor._samplers.Remove(Connection.Id);
                }
            }
            finally
            {
                StopAll();
            }
        }

        private async Task RoundAsync(CancellationToken stopping)
        {
            var now = monitor._time.GetUtcNow();
            var client = Connection.Client;
            if (client != _client)
            {
                // Reconnected (or went offline): the old client's streams are dead.
                StopAll();
                _client = client;
                _nextList = DateTimeOffset.MinValue;
            }

            if (client is null)
            {
                monitor.Publish(this, new UsageSnapshot(now, 0, 0, 0, [], "The engine is offline."));
                return;
            }

            if (now >= _nextList)
            {
                _nextList = now + ListInterval;
                await ListAsync(client, now, stopping);
            }

            monitor.Publish(this, Snapshot(now));
        }

        private async Task ListAsync(DockerClient client, DateTimeOffset now, CancellationToken stopping)
        {
            try
            {
                if (_cpus == 0)
                {
                    var info = await client.GetInfoAsync(stopping);
                    (_cpus, _hostMemory) = (info.NCpu, info.MemTotal);
                }
                // Helpers (the volume file browser) are Sockless Port's own, not what the user runs.
                var running = (await client.ListContainersAsync(all: false, stopping))
                    .Where(c => !ContainerLabels.IsHelper(c.Labels))
                    .ToList();
                _running = running.Count;
                _error = null;

                var wanted = running
                    .OrderBy(c => c.Created)
                    .Take(MaxContainers)
                    .ToDictionary(c => c.Id, StringComparer.Ordinal);

                foreach (var id in _streams.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
                {
                    _streams[id].Stop();
                    _streams.Remove(id);
                }
                foreach (var (id, summary) in wanted)
                {
                    if (_streams.TryGetValue(id, out var existing))
                    {
                        if (!existing.ShouldRestart(now))
                            continue;
                        existing.Stop();
                    }
                    var stream = new ContainerStream(id, summary.DisplayName, ContainerLabels.Stack(summary.Labels));
                    stream.Start(client, Connection.Address, monitor._time, stopping);
                    _streams[id] = stream;
                }
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested && IsEngineFailure(ex))
            {
                _error = EngineErrors.ForCall(ex, Connection.Address);
                StopAll();
            }
        }

        private UsageSnapshot Snapshot(DateTimeOffset now)
        {
            if (_error is not null)
                return new UsageSnapshot(now, _cpus, _hostMemory, _running, [], _error);
            var containers = _streams.Values.Select(s => s.Measure(_hostMemory)).ToList();
            return new UsageSnapshot(now, _cpus, _hostMemory, Math.Max(_running, containers.Count), containers);
        }

        private void StopAll()
        {
            foreach (var stream in _streams.Values)
                stream.Stop();
            _streams.Clear();
        }
    }

    /// <summary>One container's stats stream and its last two readings.</summary>
    private sealed class ContainerStream(string id, string name, string? stack)
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _cancel = new();
        private StatsSample? _latest;
        private StatsSample? _previous;
        private string? _error;
        private DateTimeOffset _failedAt;
        private bool _ended;

        public void Start(DockerClient client, DockerAddress address, TimeProvider time, CancellationToken stopping)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, stopping);
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var sample in client.StreamStatsAsync(id, linked.Token))
                    {
                        lock (_gate)
                            (_previous, _latest) = (_latest, sample);
                    }
                }
                catch (Exception ex) when (!linked.IsCancellationRequested && IsEngineFailure(ex))
                {
                    lock (_gate)
                        (_error, _failedAt) = (EngineErrors.ForCall(ex, address), time.GetUtcNow());
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (_gate)
                        _ended = true;
                    linked.Dispose();
                }
            });
        }

        /// <summary>
        /// A stream that failed is tried again after a while (the cause, such as a missing cgroup,
        /// rarely goes away). One that ended while the container is still listed as running is
        /// tried again at once (the engine closed it).
        /// </summary>
        public bool ShouldRestart(DateTimeOffset now)
        {
            lock (_gate)
                return _ended && (_error is null || now - _failedAt >= RetryAfterError);
        }

        public ContainerUsage Measure(long hostMemory)
        {
            lock (_gate)
                return ResourceMonitor.Measure(id, name, stack, _latest, _previous, hostMemory, _error);
        }

        public void Stop()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static bool IsEngineFailure(Exception ex) =>
        ex is DockerApiException or DockerApiVersionException or HttpRequestException or IOException or TimeoutException
            or TaskCanceledException or ObjectDisposedException or InvalidDataException or NotSupportedException
            or System.Text.Json.JsonException;
}
