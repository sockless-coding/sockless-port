using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Vulnerabilities;

/// <summary>Where a scan that hasn't produced a result yet stands. Switch on it exhaustively.</summary>
public closed record ScanState(int EndpointId, DateTimeOffset Since);

public sealed record ScanQueued(int EndpointId, DateTimeOffset Since) : ScanState(EndpointId, Since);

/// <param name="Phase">For people: "Updating the vulnerability database", "Copying the image (120 MB)", "Scanning".</param>
public sealed record ScanRunning(int EndpointId, string Phase, DateTimeOffset Since) : ScanState(EndpointId, Since);

/// <param name="Reason">Why, for people.</param>
public sealed record ScanFailed(int EndpointId, string Reason, DateTimeOffset Since) : ScanState(EndpointId, Since);

/// <summary>
/// Scans images for known vulnerabilities, one at a time, from a queue. An image is exported from
/// its engine (local or through the agent) to a private folder on the data volume, scanned there
/// with Trivy, and deleted. Results are stored per image ID, which names the same content on
/// every engine, so an image is scanned once wherever it runs. Queued, running and failed scans
/// are kept in memory only.
/// </summary>
public sealed partial class ImageScans(TrivyCli trivy, IServiceScopeFactory scopes, IEngineEventSink sink, IOptions<PortOptions> options,
    TimeProvider time, ILogger<ImageScans> logger) : BackgroundService
{
    /// <summary>Scans waiting at most; more are refused until some finish.</summary>
    public const int MaxQueued = 500;

    private const long ProgressStepBytes = 64L * 1024 * 1024;

    private readonly Channel<ScanRequest> _queue = Channel.CreateBounded<ScanRequest>(new BoundedChannelOptions(MaxQueued) { SingleReader = true });
    private readonly ConcurrentDictionary<string, ScanState> _states = new(StringComparer.Ordinal);

    private sealed record ScanRequest(EngineConnection Connection, string ImageId);

    /// <summary>The image's scan in progress, or its last failure; null when it's idle.</summary>
    public ScanState? StateOf(string imageId) => _states.GetValueOrDefault(Normalize(imageId));

    /// <summary>Scans queued or running for the endpoint.</summary>
    public int PendingFor(int endpointId) => _states.Values.Count(s => s.EndpointId == endpointId && s is ScanQueued or ScanRunning);

    /// <summary>Queues a scan of an image on the endpoint.</summary>
    /// <returns>False when the image is queued or being scanned already, or the queue is full.</returns>
    public bool Enqueue(EngineConnection connection, string imageId)
    {
        imageId = Normalize(imageId);
        var queued = new ScanQueued(connection.Id, time.GetUtcNow());
        // Claim the image first, so two clicks queue one scan.
        while (true)
        {
            if (_states.TryGetValue(imageId, out var current))
            {
                if (current is ScanQueued or ScanRunning || !_states.TryUpdate(imageId, queued, current))
                    return false;
                break;
            }
            if (_states.TryAdd(imageId, queued))
                break;
        }
        if (_queue.Writer.TryWrite(new ScanRequest(connection, imageId)))
            return true;
        _states.TryRemove(new KeyValuePair<string, ScanState>(imageId, queued));
        return false;
    }

    /// <summary>The stored results for the images that have one, by image ID.</summary>
    public async Task<IReadOnlyDictionary<string, ScanSummary>> SummariesAsync(IEnumerable<string> imageIds, CancellationToken cancellationToken = default)
    {
        var ids = imageIds.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new Dictionary<string, ScanSummary>();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var result = new Dictionary<string, ScanSummary>(StringComparer.Ordinal);
        // SQLite allows a limited number of parameters per statement.
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await db.ImageScans.AsNoTracking()
                .Where(s => chunk.Contains(s.ImageId))
                .Select(s => new { s.ImageId, s.ScannedAt, s.Critical, s.High, s.Medium, s.Low, s.Unknown, s.Fixable, s.Os, s.OsEndOfLife, s.ScannerVersion, s.DatabaseUpdatedAt })
                .ToListAsync(cancellationToken);
            foreach (var s in rows)
                result[s.ImageId] = new ScanSummary(s.ImageId, s.ScannedAt, new ScanCounts(s.Critical, s.High, s.Medium, s.Low, s.Unknown, s.Fixable),
                    s.Os, s.OsEndOfLife, s.ScannerVersion, s.DatabaseUpdatedAt);
        }
        return result;
    }

    /// <summary>The stored result of an image, with its findings; null when it wasn't scanned.</summary>
    public async Task<ScanResult?> GetAsync(string imageId, CancellationToken cancellationToken = default)
    {
        imageId = Normalize(imageId);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var scan = await db.ImageScans.AsNoTracking().FirstOrDefaultAsync(s => s.ImageId == imageId, cancellationToken);
        if (scan is null)
            return null;
        List<VulnerabilityFinding> findings;
        try
        {
            findings = JsonSerializer.Deserialize(scan.Findings, ScanJsonContext.Default.ListVulnerabilityFinding) ?? [];
        }
        catch (JsonException ex)
        {
            LogUnreadable(logger, imageId, ex.Message);
            findings = [];
        }
        return new ScanResult(scan.ToSummary(), findings);
    }

    /// <summary>
    /// Deletes stored results last scanned before <paramref name="before"/>, except for the
    /// images in <paramref name="keep"/> (still on an engine).
    /// </summary>
    /// <returns>How many were deleted.</returns>
    public async Task<int> PruneAsync(IReadOnlySet<string> keep, DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        // Compared in memory: dates are stored as sortable strings, and there are few rows.
        var old = (await db.ImageScans.Select(s => new { s.Id, s.ImageId, s.ScannedAt }).ToListAsync(cancellationToken))
            .Where(s => s.ScannedAt < before && !keep.Contains(s.ImageId))
            .Select(s => s.Id)
            .ToList();
        if (old.Count == 0)
            return 0;
        return await db.ImageScans.Where(s => old.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- the worker

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(request.Connection, request.ImageId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Scans one image now and stores the result. Failures become a <see cref="ScanFailed"/> state; only shutdown throws.</summary>
    internal async Task RunAsync(EngineConnection connection, string imageId, CancellationToken stoppingToken)
    {
        imageId = Normalize(imageId);
        var started = time.GetUtcNow();
        var limit = TimeSpan.FromMinutes(Math.Max(1, options.Value.Scanner.TimeoutMinutes));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(limit + TimeSpan.FromMinutes(2));
        var work = Path.Combine(options.Value.TempPath, $"scan-{Guid.NewGuid():n}");
        try
        {
            await PhaseAsync(connection, imageId, "Preparing the vulnerability database", started, notify: true);
            var status = await trivy.UpdateDatabasesAsync(timeout.Token);

            if (connection.Client is not { } client)
                throw new ScannerUnavailableException($"{connection.Name} is offline, so the image couldn't be copied from it.");
            var (image, _) = await client.InspectImageAsync(imageId, timeout.Token);

            ComposeCli.CreatePrivateDirectory(work);
            var tarball = Path.Combine(work, "image.tar");
            await PhaseAsync(connection, imageId, $"Copying the image from {connection.Name}", started, notify: true);
            await ExportAsync(connection, client, image, tarball, started, timeout.Token);

            await PhaseAsync(connection, imageId, "Scanning", started, notify: true);
            var report = await trivy.ScanAsync(tarball, work, limit, timeout.Token);
            await StoreAsync(imageId, image, report, status, timeout.Token);
            _states.TryRemove(imageId, out _);
            LogScanned(logger, ImageName(image) ?? imageId, connection.Name, report.Findings.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _states.TryRemove(imageId, out _);
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ScannerUnavailableException or DockerApiException or DockerApiVersionException
                                       or HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException or ObjectDisposedException
                                       or InvalidDataException or JsonException or DbUpdateException)
        {
            var reason = ex switch
            {
                OperationCanceledException => $"The scan took longer than {limit.TotalMinutes:0} minutes (Port:Scanner:TimeoutMinutes).",
                ScannerUnavailableException => ex.Message,
                DockerApiException { IsNotFound: true } => "The image isn't on the engine any more.",
                DockerApiException or DockerApiVersionException or HttpRequestException => EngineErrors.ForCall(ex, connection.Address),
                _ => ex.Message,
            };
            _states[imageId] = new ScanFailed(connection.Id, reason, time.GetUtcNow());
            LogFailed(logger, imageId, connection.Name, reason);
        }
        finally
        {
            TryDelete(work);
        }
        await NotifyAsync(connection);
    }

    private async Task ExportAsync(EngineConnection connection, DockerClient client, ImageInspect image, string tarball, DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        await using var source = await client.ExportImageAsync(image.Id, image.Os, image.Architecture, image.Variant, cancellationToken);
        await using var target = new FileStream(tarball, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long copied = 0, reported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (copied - reported >= ProgressStepBytes)
            {
                reported = copied;
                var total = image.Size > 0 ? $" of about {Bytes(image.Size)}" : "";
                await PhaseAsync(connection, Normalize(image.Id), $"Copying the image from {connection.Name} ({Bytes(copied)}{total})", started, notify: false);
            }
        }
    }

    internal async Task StoreAsync(string imageId, ImageInspect image, TrivyReport report, ScannerStatus status, CancellationToken cancellationToken)
    {
        var counts = ScanCounts.Of(report.Findings);
        var findings = JsonSerializer.Serialize([.. report.Findings], ScanJsonContext.Default.ListVulnerabilityFinding);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var scan = await db.ImageScans.FirstOrDefaultAsync(s => s.ImageId == imageId, cancellationToken);
        if (scan is null)
        {
            scan = new ImageScan { ImageId = imageId, ScannerVersion = status.Version, Findings = findings };
            db.ImageScans.Add(scan);
        }
        scan.ImageName = ImageName(image);
        scan.ScannedAt = time.GetUtcNow();
        scan.ScannerVersion = status.Version;
        scan.DatabaseUpdatedAt = status.Database?.UpdatedAt;
        scan.Os = report.Os;
        scan.OsEndOfLife = report.OsEndOfLife;
        scan.Critical = counts.Critical;
        scan.High = counts.High;
        scan.Medium = counts.Medium;
        scan.Low = counts.Low;
        scan.Unknown = counts.Unknown;
        scan.Fixable = counts.Fixable;
        scan.Findings = findings;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PhaseAsync(EngineConnection connection, string imageId, string phase, DateTimeOffset started, bool notify)
    {
        _states[imageId] = new ScanRunning(connection.Id, phase, started);
        if (notify)
            await NotifyAsync(connection);
    }

    /// <summary>Open pages of the endpoint refresh, as they do for engine events.</summary>
    private async Task NotifyAsync(EngineConnection connection)
    {
        try
        {
            await sink.EngineEventAsync(connection, new DockerEvent { Type = "image", Action = "scan" }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A browser that's gone must not fail a scan.
            LogNotifyFailed(logger, ex.Message);
        }
    }

    private static string? ImageName(ImageInspect image) => image.RepoTags?.FirstOrDefault(t => t != "<none>:<none>");

    /// <summary>Image IDs as the engines report them: <c>sha256:</c> and 64 hex digits.</summary>
    public static string Normalize(string imageId) => imageId.Contains(':', StringComparison.Ordinal) ? imageId : "sha256:" + imageId;

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} kB",
    };

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(logger, directory, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned {Image} from {Endpoint}: {Count} known vulnerabilities")]
    private static partial void LogScanned(ILogger logger, string image, string endpoint, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't scan {Image} from {Endpoint}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string image, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored scan of {Image} is unreadable: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string image, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't tell browsers a scan changed: {Reason}")]
    private static partial void LogNotifyFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the scan folder {Path}: {Reason}")]
    private static partial void LogDeleteFailed(ILogger logger, string path, string reason);
}
