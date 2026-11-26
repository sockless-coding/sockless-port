using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Agents;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;

namespace Sockless.Port.Notifications;

/// <summary>
/// Turns what happens into alerts for the <see cref="Notifier"/>: engine events (a container that
/// exits with an error, restarts again and again, or turns unhealthy), endpoint states (offline
/// for two minutes, an outdated agent), and results from scans, scheduled update checks, stack
/// jobs and the resource history. Helper containers never alert. Its own timer clears restart
/// alerts after quiet time and raises offline ones after the grace period.
/// </summary>
/// <param name="settings">Whether agents update automatically: then an outdated agent doesn't alert (a failed update does).</param>
public sealed partial class Alerts(Notifier notifier, EngineConnections connections, TimeProvider time, ILogger<Alerts> logger,
    Settings.PortSettings? settings = null) : BackgroundService
{
    /// <summary>An endpoint must stay offline this long before it alerts: engines restart, networks blip.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(2);

    /// <summary>This many deaths within <see cref="RestartWindow"/> is a restart loop.</summary>
    public const int RestartCount = 3;

    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    /// <summary>A stop that follows a kill or stop request this soon was asked for.</summary>
    internal static readonly TimeSpan StopRequestWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(int Endpoint, string Container), DateTimeOffset> _stopRequested = new();
    private readonly ConcurrentDictionary<(int Endpoint, string Container), bool> _outOfMemory = new();
    private readonly ConcurrentDictionary<(int Endpoint, string Name), List<DateTimeOffset>> _deaths = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _offlineSince = new();
    private readonly ConcurrentDictionary<(int Rule, int Endpoint, string Subject, string Metric), int> _minutesAbove = new();

    // Engine events.

    /// <summary>An event from an endpoint's engine (all of them, not only those the UI shows).</summary>
    public async Task EngineEventAsync(EngineConnection connection, DockerEvent e, CancellationToken cancellationToken = default)
    {
        if (e.Type != "container" || e.Actor is not { Id: { } id } actor || ContainerLabels.IsHelper(actor.Attributes))
            return;
        var name = actor.Attributes?.GetValueOrDefault("name") ?? id[..Math.Min(12, id.Length)];
        var at = e.Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Time) : time.GetUtcNow();
        var ep = connection.Id;
        var link = $"/endpoints/{ep}/containers/{Uri.EscapeDataString(name)}";

        switch (e.Action)
        {
            case "kill" or "stop" or "pause":
                _stopRequested[(ep, id)] = at;
                break;
            case "oom":
                _outOfMemory[(ep, id)] = true;
                break;
            case "die":
            {
                var requested = _stopRequested.TryRemove((ep, id), out var when) && at - when <= StopRequestWindow;
                var oom = _outOfMemory.TryRemove((ep, id), out _);
                var code = actor.Attributes?.GetValueOrDefault("exitCode");
                if (requested && !oom)
                    break;

                var deaths = _deaths.GetOrAdd((ep, name), _ => []);
                int count;
                lock (deaths)
                {
                    deaths.RemoveAll(d => at - d > RestartWindow);
                    deaths.Add(at);
                    count = deaths.Count;
                }
                if (count >= RestartCount)
                {
                    await notifier.RaiseAsync(new Alert(AlertKind.ContainerRestarting, $"restarting:{ep}:{name}", ep, connection.Name, AlertSeverity.Critical,
                        $"{name} keeps restarting on {connection.Name}",
                        $"It stopped {count} times in the last {RestartWindow.TotalMinutes:0} minutes{(code is null ? "" : $", the last time with exit code {code}")}. Its logs say why.",
                        link), cancellationToken);
                }
                // In a restart loop, the loop is the news: its exits would only repeat it.
                if ((oom || (code is not null && code != "0")) && !notifier.IsActive($"restarting:{ep}:{name}"))
                {
                    await notifier.RaiseAsync(new Alert(AlertKind.ContainerExited, $"exited:{ep}:{name}", ep, connection.Name, AlertSeverity.Warning,
                        oom ? $"{name} ran out of memory on {connection.Name}" : $"{name} exited with code {code} on {connection.Name}",
                        (oom ? "The kernel stopped it for using more memory than it may." : ExitMeaning(code))
                        + (actor.Attributes?.GetValueOrDefault("image") is { } image ? $"\nImage: {image}" : ""),
                        link), cancellationToken);
                }
                break;
            }
            case "start":
                // Running again, unless it's looping: then it will be over only when it stays up.
                await notifier.ClearAsync($"exited:{ep}:{name}", announce: !notifier.IsActive($"restarting:{ep}:{name}"), cancellationToken);
                break;
            case "destroy":
                _deaths.TryRemove((ep, name), out _);
                _stopRequested.TryRemove((ep, id), out _);
                await notifier.ClearAsync($"exited:{ep}:{name}", announce: false, cancellationToken);
                await notifier.ClearAsync($"restarting:{ep}:{name}", announce: false, cancellationToken);
                await notifier.ClearAsync($"unhealthy:{ep}:{name}", announce: false, cancellationToken);
                break;
            case "health_status: unhealthy":
                await notifier.RaiseAsync(new Alert(AlertKind.ContainerUnhealthy, $"unhealthy:{ep}:{name}", ep, connection.Name, AlertSeverity.Warning,
                    $"{name} is unhealthy on {connection.Name}", "Its health check is failing. The container's page shows the check's output.", link), cancellationToken);
                break;
            case "health_status: healthy":
                await notifier.ClearAsync($"unhealthy:{ep}:{name}", cancellationToken: cancellationToken);
                break;
        }
    }

    private static string ExitMeaning(string? code) => code switch
    {
        "137" => "Exit code 137: it was killed (SIGKILL), often for running out of memory.",
        "139" => "Exit code 139: it crashed (segmentation fault).",
        "143" => "Exit code 143: it was terminated (SIGTERM).",
        "126" or "127" => $"Exit code {code}: its command couldn't be run or wasn't found.",
        _ => "It stopped by itself, with an error.",
    };

    // Endpoint state.

    /// <summary>An endpoint's connection changed.</summary>
    public async Task StateChangedAsync(EngineConnection connection, CancellationToken cancellationToken = default)
    {
        switch (connection.State)
        {
            case EndpointOnline:
                _offlineSince.TryRemove(connection.Id, out _);
                await notifier.ClearAsync($"offline:{connection.Id}", cancellationToken: cancellationToken);
                if (connection.Agent?.Info?.Version is { } version && AgentCommands.IsOutdated(version) && settings?.Current.AgentAutoUpdate != true)
                {
                    await notifier.RaiseAsync(new Alert(AlertKind.AgentOutdated, $"agent:{connection.Id}:{version}", connection.Id, connection.Name, AlertSeverity.Info,
                        $"The agent on {connection.Name} is out of date",
                        $"It runs version {version}; the server is {AgentCommands.ServerVersion}. Update it from the endpoint's page, or turn on automatic agent updates in Settings.",
                        $"/endpoints/{connection.Id}/settings"), cancellationToken);
                }
                break;
            case EndpointOffline offline:
                _offlineSince.TryAdd(connection.Id, offline.Since);
                break;
            case EndpointConnecting or EndpointEnrolling:
                break;
        }
    }

    /// <summary>Raises offline alerts past the grace period, and clears restart alerts that went quiet.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var (id, since) in _offlineSince)
        {
            if (connections.Get(id) is not { } connection)
            {
                _offlineSince.TryRemove(id, out _);
                continue;
            }
            if (connection.State is not EndpointOffline offline)
                continue;
            if (now - since >= OfflineGrace)
            {
                await notifier.RaiseAsync(new Alert(AlertKind.EndpointOffline, $"offline:{id}", id, connection.Name, AlertSeverity.Critical,
                    $"{connection.Name} is offline", $"Sockless Port can't reach its engine: {offline.Reason}", $"/endpoints/{id}"), cancellationToken);
            }
        }

        foreach (var ((endpoint, name), deaths) in _deaths)
        {
            bool quiet;
            lock (deaths)
            {
                deaths.RemoveAll(d => now - d > RestartWindow);
                quiet = deaths.Count == 0;
            }
            if (quiet)
            {
                _deaths.TryRemove((endpoint, name), out _);
                await notifier.ClearAsync($"restarting:{endpoint}:{name}", cancellationToken: cancellationToken);
            }
        }
        foreach (var (key, at) in _stopRequested)
        {
            if (now - at > StopRequestWindow)
                _stopRequested.TryRemove(key, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogSweepFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Results of background work.

    /// <summary>A scan found critical or high vulnerabilities the image's previous scan didn't have (accepted ones left out).</summary>
    public Task NewVulnerabilitiesAsync(EngineConnection connection, string imageId, string imageName, IReadOnlyCollection<(string Id, string Severity)> findings,
        CancellationToken cancellationToken = default)
    {
        if (findings.Count == 0)
            return Task.CompletedTask;
        var critical = findings.Count(f => f.Severity == "Critical");
        var high = findings.Count - critical;
        var ids = findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var counts = string.Join(" and ", new[] { critical > 0 ? $"{critical} critical" : null, high > 0 ? $"{high} high" : null }.OfType<string>());
        return notifier.RaiseAsync(new Alert(AlertKind.Vulnerabilities, $"vulnerabilities:{imageId}:{Hash(ids)}", connection.Id, connection.Name,
            critical > 0 ? AlertSeverity.Critical : AlertSeverity.Warning,
            $"{imageName} has {counts} new {(findings.Count == 1 ? "vulnerability" : "vulnerabilities")}",
            $"Found on {connection.Name}: {string.Join(", ", ids.Take(10))}{(ids.Count > 10 ? $" and {ids.Count - 10} more" : "")}.",
            $"/endpoints/{connection.Id}/images/{Uri.EscapeDataString(imageId)}/vulnerabilities"), cancellationToken);
    }

    /// <summary>A scheduled check finished; <paramref name="references"/> are the images with an update (an alert when that list changes).</summary>
    public async Task UpdatesCheckedAsync(EngineConnection connection, IReadOnlyCollection<string> references, CancellationToken cancellationToken = default)
    {
        var sorted = references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var prefix = $"updates:{connection.Id}:";
        var key = prefix + Hash(sorted);
        // The same list as last time: nothing new to say.
        if (sorted.Count > 0 && notifier.IsActive(key))
            return;
        await notifier.ClearPrefixAsync(prefix, announce: false, cancellationToken);
        if (sorted.Count == 0)
            return;
        await notifier.RaiseAsync(new Alert(AlertKind.UpdatesAvailable, key, connection.Id, connection.Name, AlertSeverity.Info,
            $"{sorted.Count} {(sorted.Count == 1 ? "image has an update" : "images have updates")} on {connection.Name}",
            string.Join('\n', sorted.Take(20)) + (sorted.Count > 20 ? $"\n…and {sorted.Count - 20} more" : ""),
            $"/endpoints/{connection.Id}/images"), cancellationToken);
    }

    /// <summary>A stack job or a scheduled check failed. Each failure is its own alert.</summary>
    public Task TaskFailedAsync(EngineConnection connection, string title, string error, string? link, CancellationToken cancellationToken = default) =>
        notifier.RaiseAsync(new Alert(AlertKind.TaskFailed, $"task:{connection.Id}:{Guid.NewGuid():n}", connection.Id, connection.Name, AlertSeverity.Warning,
            title, error, link), cancellationToken);

    /// <summary>A backup to the backup folder failed. Fires once until a backup works again.</summary>
    public Task BackupFailedAsync(string error, CancellationToken cancellationToken = default) =>
        notifier.RaiseAsync(new Alert(AlertKind.BackupFailed, BackupKey, null, null, AlertSeverity.Critical, "A backup of Sockless Port failed", error, "/maintenance"),
            cancellationToken);

    /// <summary>A backup worked: a failure alert is resolved.</summary>
    public Task BackupSucceededAsync(CancellationToken cancellationToken = default) => notifier.ClearAsync(BackupKey, announce: true, cancellationToken);

    private const string BackupKey = "backup";

    /// <summary>One minute of resource use was recorded: checks the rules' thresholds against it.</summary>
    public async Task ResourceMinuteAsync(EngineConnection connection, ResourceTotalEntry totals, IReadOnlyCollection<ResourceUsageEntry> containers,
        CancellationToken cancellationToken = default)
    {
        var rules = await notifier.ResourceRulesAsync(connection.Id, cancellationToken);
        if (rules.Count == 0 || totals.Cpus <= 0 || totals.HostMemory <= 0)
            return;

        var subjects = new List<(string Subject, string Label, double Cpu, double Memory, string Link)>
        {
            ("", $"All containers on {connection.Name}", totals.Cpu / totals.Cpus, totals.Memory * 100.0 / totals.HostMemory, $"/endpoints/{connection.Id}/resources"),
        };
        subjects.AddRange(containers.Select(c => (c.Name, $"{c.Name} on {connection.Name}", c.Cpu / totals.Cpus, c.Memory * 100.0 / totals.HostMemory,
            $"/endpoints/{connection.Id}/containers/{Uri.EscapeDataString(c.Name)}")));

        foreach (var rule in rules)
        {
            foreach (var (subject, label, cpu, memory, link) in subjects)
            {
                if (rule.CpuPercent is { } cpuLimit)
                    await CheckAsync(rule, subject, "cpu", label, "CPU", cpu, cpuLimit, link);
                if (rule.MemoryPercent is { } memoryLimit)
                    await CheckAsync(rule, subject, "memory", label, "memory", memory, memoryLimit, link);
            }
        }

        async Task CheckAsync(NotificationRule rule, string subject, string metric, string label, string what, double value, int limit, string link)
        {
            var state = (rule.Id, connection.Id, subject, metric);
            var key = $"resource:{rule.Id}:{connection.Id}:{metric}:{subject}";
            if (value < limit)
            {
                _minutesAbove.TryRemove(state, out _);
                await notifier.ClearAsync(key, cancellationToken: cancellationToken);
                return;
            }
            var minutes = _minutesAbove.AddOrUpdate(state, 1, (_, n) => n + 1);
            if (minutes < rule.ForMinutes)
                return;
            await notifier.RaiseAsync(new Alert(AlertKind.ResourceUsage, key, connection.Id, connection.Name, AlertSeverity.Warning,
                $"{label}: {what} above {limit}%",
                string.Create(CultureInfo.InvariantCulture, $"{what[..1].ToUpperInvariant()}{what[1..]} has been at least {limit}% of the host's for {minutes} minutes (now {value:0.#}%)."),
                link, rule.Id), cancellationToken);
        }
    }

    private static string Hash(IEnumerable<string> items) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', items))))[..16];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for alerts failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
