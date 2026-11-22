using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Stacks;

namespace Sockless.Port.Vulnerabilities;

/// <summary>Trivy isn't installed, can't run, or has no database to scan with. The message says what to do.</summary>
public sealed class ScannerUnavailableException(string message) : Exception(message);

/// <summary>A vulnerability database in Trivy's cache, from <c>trivy version --format json</c>.</summary>
public sealed record ScannerDatabase(DateTimeOffset? UpdatedAt, DateTimeOffset? NextUpdate, DateTimeOffset? DownloadedAt)
{
    public bool IsDue(DateTimeOffset now) => NextUpdate is not { } next || next <= now;
}

/// <summary>The scanner found on this server and the databases it has.</summary>
/// <param name="Database">The vulnerability database; null before the first download.</param>
/// <param name="JavaDatabase">The database for Java packages (jar files); null before the first download.</param>
public sealed record ScannerStatus(string Version, ScannerDatabase? Database, ScannerDatabase? JavaDatabase);

/// <summary>
/// Runs Trivy as a child process, the way <see cref="ComposeCli"/> runs Compose: an argument list,
/// a clean environment (a private home, plus the proxy variables so downloads work behind one),
/// and the process tree killed on cancellation. The databases live in
/// <see cref="PortOptions.ScannerCachePath"/>; scans never download them, only
/// <see cref="UpdateDatabasesAsync"/> does, one at a time.
/// </summary>
public sealed class TrivyCli(IOptions<PortOptions> options, TimeProvider time, ILogger<TrivyCli> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Passed through to Trivy so database downloads work behind a proxy.</summary>
    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR"];

    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private PortOptions Port => options.Value;

    private ScannerOptions Scanner => options.Value.Scanner;

    internal string Command => Scanner.Command is { Length: > 0 } command ? command.Trim() : "trivy";

    /// <summary>The scanner's version and databases.</summary>
    /// <exception cref="ScannerUnavailableException">Trivy isn't installed or doesn't run.</exception>
    public async Task<ScannerStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["version", "--format", "json", "--cache-dir", Port.ScannerCachePath], ProbeTimeout, "answer", cancellationToken);
        if (!result.Succeeded)
            throw new ScannerUnavailableException($"{Command} version failed: {Tail(result)}");
        try
        {
            using var json = JsonDocument.Parse(result.Stdout);
            return ParseStatus(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new ScannerUnavailableException($"{Command} didn't report its version as Trivy does: {Tail(result)}");
        }
    }

    /// <summary>The scanner's status, or why it can't be used, without throwing.</summary>
    public async Task<(ScannerStatus? Status, string? Problem)> TryStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await StatusAsync(cancellationToken), null);
        }
        catch (ScannerUnavailableException ex)
        {
            return (null, ex.Message);
        }
    }

    internal static ScannerStatus ParseStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || version.GetString() is not { Length: > 0 } text)
            throw new InvalidDataException("No version.");
        return new ScannerStatus(text.TrimStart('v'), Database(root, "VulnerabilityDB"), Database(root, "JavaDB"));
    }

    private static ScannerDatabase? Database(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var db) || db.ValueKind != JsonValueKind.Object)
            return null;
        var database = new ScannerDatabase(Time(db, "UpdatedAt"), Time(db, "NextUpdate"), Time(db, "DownloadedAt"));
        // Trivy reports zero times for a database it doesn't have.
        return database.UpdatedAt is null && database.DownloadedAt is null ? null : database;
    }

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at.Year > 1
            ? at
            : null;

    /// <summary>
    /// Downloads the databases that are missing or past their next update, unless downloads are
    /// off. A failed download is fine while an older database is there to scan with.
    /// </summary>
    /// <returns>The status after the update.</returns>
    /// <exception cref="ScannerUnavailableException">There's no database to scan with.</exception>
    public async Task<ScannerStatus> UpdateDatabasesAsync(CancellationToken cancellationToken = default)
    {
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            var status = await StatusAsync(cancellationToken);
            if (Scanner.SkipDatabaseUpdate)
                return status.Database is null
                    ? throw new ScannerUnavailableException(
                        $"There's no vulnerability database in {Port.ScannerCachePath}, and Port:Scanner:SkipDatabaseUpdate stops Sockless Port from downloading one. Copy one there (see the docs), or allow the download.")
                    : status;

            var now = time.GetUtcNow();
            if (status.Database?.IsDue(now) != false)
                await DownloadAsync(DatabaseArguments(java: false), "vulnerability database", status.Database is not null, cancellationToken);
            if (status.JavaDatabase?.IsDue(now) != false)
                await DownloadAsync(DatabaseArguments(java: true), "Java database", status.JavaDatabase is not null, cancellationToken);
            return await StatusAsync(cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    internal List<string> DatabaseArguments(bool java)
    {
        List<string> arguments = ["image", java ? "--download-java-db-only" : "--download-db-only", "--cache-dir", Port.ScannerCachePath, "--no-progress", "--quiet"];
        foreach (var repository in (java ? Scanner.JavaDatabaseRepositories : Scanner.DatabaseRepositories).Where(r => !string.IsNullOrWhiteSpace(r)))
            arguments.AddRange([java ? "--java-db-repository" : "--db-repository", repository.Trim()]);
        return arguments;
    }

    private async Task DownloadAsync(List<string> arguments, string what, bool haveOlder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Port.ScannerCachePath);
        ComposeResult result;
        try
        {
            result = await RunAsync(arguments, DownloadTimeout, $"finish downloading the {what}", cancellationToken);
        }
        catch (ScannerUnavailableException) when (haveOlder && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Downloading the {What} timed out; scanning with the one downloaded before.", what);
            return;
        }
        if (result.Succeeded)
        {
            logger.LogInformation("Downloaded the scanner's {What}.", what);
            return;
        }
        if (haveOlder)
        {
            logger.LogWarning("Couldn't download the {What}; scanning with the one downloaded before: {Reason}", what, Tail(result));
            return;
        }
        throw new ScannerUnavailableException($"Couldn't download the scanner's {what}: {Tail(result)} " +
                                              "Without internet access, set Port:Scanner:DatabaseRepositories to a mirror, or copy the database in and set Port:Scanner:SkipDatabaseUpdate.");
    }

    /// <summary>Scans an image tarball and reads the report. The databases must be there (<see cref="UpdateDatabasesAsync"/>).</summary>
    /// <param name="tarball">A <c>docker save</c> or OCI archive.</param>
    /// <param name="workDirectory">A private folder for the report, deleted by the caller.</param>
    /// <exception cref="ScannerUnavailableException">Trivy failed; the message has its last lines.</exception>
    public async Task<TrivyReport> ScanAsync(string tarball, string workDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var report = Path.Combine(workDirectory, "report.json");
        // Trivy has its own --timeout; ours is a little longer, for a scanner that hangs.
        var result = await RunAsync(ScanArguments(tarball, report, timeout), timeout + TimeSpan.FromMinutes(1), "finish the scan", cancellationToken, workDirectory);
        if (!result.Succeeded)
            throw new ScannerUnavailableException($"The scan failed: {Tail(result)}");
        if (!File.Exists(report))
            throw new ScannerUnavailableException("The scanner finished without writing a report.");
        await using var file = File.OpenRead(report);
        try
        {
            using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken);
            return TrivyReport.Parse(json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ScannerUnavailableException($"The scanner's report isn't valid JSON: {ex.Message}");
        }
    }

    internal List<string> ScanArguments(string tarball, string report, TimeSpan timeout)
    {
        List<string> arguments =
        [
            "image", "--input", tarball, "--format", "json", "--output", report,
            "--scanners", "vuln", "--cache-dir", Port.ScannerCachePath,
            // Layers are analysed again next time instead of filling the data volume.
            "--cache-backend", "memory",
            "--skip-db-update", "--skip-java-db-update",
            "--no-progress", "--quiet",
            "--timeout", $"{Math.Max(1, (int)timeout.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m",
        ];
        if (Scanner.SkipDatabaseUpdate)
            arguments.Add("--offline-scan");
        return arguments;
    }

    /// <summary>Runs Trivy with <paramref name="timeout"/> on top of <paramref name="cancellationToken"/>; running out of time is a <see cref="ScannerUnavailableException"/>.</summary>
    /// <param name="verb">What didn't happen in time: "answer", "finish downloading"…</param>
    private async Task<ComposeResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, string verb, CancellationToken cancellationToken, string? workDirectory = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            return await RunAsync(arguments, limit.Token, workDirectory);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScannerUnavailableException($"{Command} didn't {verb} within {Format(timeout)}.");
        }
    }

    private static string Format(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes"
        : $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds";

    private async Task<ComposeResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, string? workDirectory)
    {
        var runDirectory = workDirectory ?? Path.Combine(Port.TempPath, $"trivy-{Guid.NewGuid():n}");
        ComposeCli.CreatePrivateDirectory(runDirectory);
        try
        {
            var start = ComposeCli.StartInfo(Command, arguments);
            start.WorkingDirectory = runDirectory;
            start.Environment.Clear();
            foreach (var (key, value) in Environment(runDirectory))
                start.Environment[key] = value;
            return await ComposeCli.ExecuteAsync(start, onLine: null, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ScannerUnavailableException(Scanner.Command is { Length: > 0 }
                ? $"Trivy isn't at '{Command}'. Check the Port:Scanner:Command setting."
                : "Trivy isn't installed on the Sockless Port server. The image includes it; when running from source, install trivy or set Port:Scanner:Command.");
        }
        finally
        {
            if (workDirectory is null)
                TryDelete(runDirectory);
        }
    }

    internal static Dictionary<string, string> Environment(string runDirectory)
    {
        var env = ComposeCli.BaseEnvironment(runDirectory);
        foreach (var key in ProxyVariables)
        {
            if (System.Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                env[key] = value;
        }
        return env;
    }

    private static string Tail(ComposeResult result)
    {
        var lines = result.Output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3).ToList();
        return lines.Count == 0 ? $"it exited with code {result.ExitCode}." : string.Join(' ', lines);
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't delete the scanner's run folder {Path}", directory);
        }
    }
}
