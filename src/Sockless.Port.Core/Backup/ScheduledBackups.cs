using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Maintenance;
using Sockless.Port.Settings;

namespace Sockless.Port.Backup;

/// <summary>How backups are taken on a schedule.</summary>
/// <param name="IntervalHours">24 (daily) or 168 (weekly); 0 is off.</param>
/// <param name="Keep">How many scheduled backups the folder keeps; older ones are deleted.</param>
/// <param name="Folder">As entered: empty for <c>{DataPath}/backups</c>, relative to the data path, or absolute (a mounted path).</param>
/// <param name="Encrypted">A passphrase is stored, and backups are encrypted with it.</param>
public sealed record BackupSchedule(int IntervalHours, int Keep, string Folder, bool Encrypted)
{
    public static BackupSchedule Default { get; } = new(0, 7, "", false);

    public static readonly IReadOnlyList<int> IntervalChoices = [0, 24, 7 * 24];

    public const int MaxKeep = 365;
    public const int MaxFolder = 512;
}

/// <summary>A backup in the schedule's folder.</summary>
public sealed record StoredBackup(string Name, long Size, DateTimeOffset CreatedAt, bool Encrypted);

public sealed record BackupRunResult(MaintenanceOutcome Outcome, string Summary, string? FileName);

/// <summary>A backup is already being written.</summary>
public sealed class BackupBusyException() : Exception("A backup is already being written.");

/// <summary>
/// Scheduled backups: written to a folder (in the data volume or a mounted path), optionally
/// encrypted with a passphrase stored with Data Protection, and kept to a count. Only files this
/// writes (<c>sockless-port-backup-{time}.tar.gz[.enc]</c>) are ever deleted. A failure raises a
/// backup-failed alert, resolved by the next backup that works.
/// </summary>
public sealed partial class ScheduledBackups(DataBackup backup, IServiceScopeFactory scopes, IDataProtectionProvider protection, IOptions<PortOptions> options,
    MaintenanceLog log, AuditLog audit, TimeProvider time, ILogger<ScheduledBackups> logger,
    Work.WorkTracker? work = null, Notifications.Alerts? alerts = null)
{
    private const string IntervalKey = "backup-interval-hours";
    private const string KeepKey = "backup-keep";
    private const string FolderKey = "backup-folder";
    private const string PassphraseKey = "backup-passphrase";
    private const string Purpose = "Sockless.Port.Backup.Passphrase";
    private const string PartialSuffix = ".partial";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);
    private readonly SemaphoreSlim _writing = new(1, 1);

    private PortOptions Port => options.Value;

    public bool IsRunning => _writing.CurrentCount == 0;

    public async Task<BackupSchedule> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(cancellationToken);
        var defaults = BackupSchedule.Default;
        return new BackupSchedule(
            Int(stored, IntervalKey) ?? defaults.IntervalHours,
            Int(stored, KeepKey) ?? defaults.Keep,
            stored.GetValueOrDefault(FolderKey) ?? defaults.Folder,
            stored.ContainsKey(PassphraseKey));
    }

    /// <summary>The folder backups go to, resolved: empty is <c>{DataPath}/backups</c>, relative is under the data path.</summary>
    public string ResolveFolder(string? folder) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? Path.Combine(Port.DataPath, "backups") : Path.Combine(Port.DataPath, folder.Trim()));

    /// <summary>What's wrong with the folder: one Sockless Port manages itself, or one it can't write to.</summary>
    public string? FolderProblem(string? folder)
    {
        if (folder is { Length: > BackupSchedule.MaxFolder })
            return $"Use at most {BackupSchedule.MaxFolder} characters.";
        string resolved;
        try
        {
            resolved = ResolveFolder(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That isn't a valid path.";
        }
        var data = Path.GetFullPath(Port.DataPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar), data.TrimEnd(Path.DirectorySeparatorChar), comparison))
            return "Choose a folder of its own, not the data folder itself.";
        string[] managed = [.. DataBackup.Folders.Select(f => Path.Combine(data, f)), Port.GitPath, Port.TempPath, Port.RestorePendingPath, Port.ScannerCachePath];
        foreach (var path in managed.Select(Path.GetFullPath))
        {
            if (resolved.Equals(path, comparison) || resolved.StartsWith(path + Path.DirectorySeparatorChar, comparison))
                return $"Sockless Port manages {path} itself; choose another folder.";
        }
        try
        {
            Directory.CreateDirectory(resolved);
            var probe = Path.Combine(resolved, $".write-test-{Guid.NewGuid():n}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Sockless Port can't write to {resolved}: {ex.Message}";
        }
        return null;
    }

    /// <param name="passphrase">A new passphrase; null keeps the stored one (if <paramref name="encrypt"/>).</param>
    /// <returns>Problems by field; nothing was saved then.</returns>
    public async Task<IReadOnlyList<(string Field, string Message)>> SaveAsync(int intervalHours, int keep, string? folder, bool encrypt, string? passphrase,
        CancellationToken cancellationToken = default)
    {
        List<(string, string)> errors = [];
        if (!BackupSchedule.IntervalChoices.Contains(intervalHours))
            errors.Add(("BackupIntervalHours", "Choose how often from the list."));
        if (keep is < 1 or > BackupSchedule.MaxKeep)
            errors.Add(("BackupKeep", $"Keep 1 to {BackupSchedule.MaxKeep} backups."));
        if (FolderProblem(folder) is { } folderProblem)
            errors.Add(("BackupFolder", folderProblem));
        var stored = await ReadAsync(cancellationToken);
        if (encrypt && string.IsNullOrEmpty(passphrase) && !stored.ContainsKey(PassphraseKey))
            errors.Add(("BackupPassphrase", "Enter a passphrase to encrypt backups with."));
        if (encrypt && !string.IsNullOrEmpty(passphrase) && BackupCipher.PassphraseProblem(passphrase) is { } passphraseProblem)
            errors.Add(("BackupPassphrase", passphraseProblem));
        if (errors.Count > 0)
            return errors;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var values = new Dictionary<string, string?>
        {
            [IntervalKey] = intervalHours.ToString(CultureInfo.InvariantCulture),
            [KeepKey] = keep.ToString(CultureInfo.InvariantCulture),
            [FolderKey] = folder?.Trim() ?? "",
            [PassphraseKey] = !encrypt ? null : string.IsNullOrEmpty(passphrase) ? stored[PassphraseKey] : _protector.Protect(passphrase),
        };
        var existing = await db.Settings.Where(s => values.Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, cancellationToken);
        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var entry))
            {
                if (value is null)
                    db.Settings.Remove(entry);
                else
                    entry.Value = value;
            }
            else if (value is not null)
            {
                db.Settings.Add(new SettingEntry { Key = key, Value = value });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return [];
    }

    /// <summary>The stored passphrase, or null when backups aren't encrypted.</summary>
    /// <exception cref="CryptographicException">The keys that encrypted it are gone.</exception>
    public async Task<string?> PassphraseAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(cancellationToken);
        return stored.TryGetValue(PassphraseKey, out var value) ? _protector.Unprotect(value) : null;
    }

    /// <summary>When the next scheduled backup is due: the last success plus the interval, an hour after a failure; null when off.</summary>
    public async Task<DateTimeOffset?> NextAsync(BackupSchedule schedule, CancellationToken cancellationToken = default)
    {
        if (schedule.IntervalHours <= 0)
            return null;
        var latest = (await log.LatestAsync(cancellationToken)).GetValueOrDefault((MaintenanceTask.Backup, null));
        var success = await log.LastSucceededAsync(MaintenanceTask.Backup, null, cancellationToken);
        return Next(schedule, latest, success, time.GetUtcNow());
    }

    internal static DateTimeOffset Next(BackupSchedule schedule, MaintenanceRun? latest, MaintenanceRun? lastSuccess, DateTimeOffset now)
    {
        var due = lastSuccess is null ? now : lastSuccess.StartedAt + TimeSpan.FromHours(schedule.IntervalHours);
        if (latest is { Outcome: MaintenanceOutcome.Failed } failed && failed.StartedAt + RetryAfterFailure > due)
            due = failed.StartedAt + RetryAfterFailure;
        return due;
    }

    /// <summary>How soon a failed scheduled backup is tried again.</summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Writes a backup to the folder, then deletes the oldest beyond the count to keep.</summary>
    /// <exception cref="BackupBusyException">One is already being written.</exception>
    public async Task<BackupRunResult> RunAsync(MaintenanceTrigger trigger, CancellationToken cancellationToken = default)
    {
        if (!await _writing.WaitAsync(0, cancellationToken))
            throw new BackupBusyException();
        using var item = work?.Start(Work.WorkKind.Backup, "Backing up Sockless Port", by: trigger.By);
        try
        {
            var started = time.GetUtcNow();
            var schedule = await GetAsync(cancellationToken);
            var result = await WriteAsync(schedule, cancellationToken);
            if (result.Outcome == MaintenanceOutcome.Succeeded)
                item?.Succeed(result.Summary);
            else
                item?.Fail(result.Summary);
            await log.RecordAsync(new MaintenanceRun
            {
                Task = MaintenanceTask.Backup,
                StartedAt = started,
                FinishedAt = time.GetUtcNow(),
                Outcome = result.Outcome,
                By = trigger.By,
                Summary = result.Summary,
            });
            await audit.WriteAsync(new AuditRecord(trigger.UserId, trigger.By, "Maintenance: back up", result.FileName, Outcome: result.Outcome == MaintenanceOutcome.Succeeded
                ? AuditOutcome.Succeeded : AuditOutcome.Failed, Detail: result.Summary, RemoteAddress: trigger.RemoteAddress));
            if (alerts is not null)
            {
                if (result.Outcome == MaintenanceOutcome.Succeeded)
                    await alerts.BackupSucceededAsync(CancellationToken.None);
                else
                    await alerts.BackupFailedAsync(result.Summary, CancellationToken.None);
            }
            return result;
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task<BackupRunResult> WriteAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        string? passphrase;
        try
        {
            passphrase = await PassphraseAsync(cancellationToken);
        }
        catch (CryptographicException)
        {
            return new(MaintenanceOutcome.Failed, "The stored passphrase can't be read (the encryption keys changed). Enter it again in Settings.", null);
        }

        var folder = ResolveFolder(schedule.Folder);
        var name = backup.FileName(encrypted: passphrase is not null);
        var path = Path.Combine(folder, name);
        var partial = path + PartialSuffix;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var stale in Directory.EnumerateFiles(folder, DataBackup.FilePrefix + "*" + PartialSuffix))
                File.Delete(stale);
            if (File.Exists(path))
                return new(MaintenanceOutcome.Failed, $"{name} already exists; try again in a second.", null);

            long size;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await backup.WriteAsync(file, AgentCommands.ServerVersion, passphrase, cancellationToken);
                await file.FlushAsync(cancellationToken);
                size = file.Length;
            }
            File.Move(partial, path);

            var removed = Rotate(folder, schedule.Keep);
            var summary = $"Wrote {name} ({PruneRunner.Bytes(size)}){(passphrase is not null ? ", encrypted" : "")} to {folder}.";
            if (removed.Count > 0)
                summary += $" Deleted the oldest: {PruneRunner.Names(removed)}.";
            LogWritten(logger, path);
            return new(MaintenanceOutcome.Succeeded, summary, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                       && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            LogFailed(logger, ex);
            return new(MaintenanceOutcome.Failed, $"Couldn't write a backup to {folder}: {ex.Message}", null);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Deletes scheduled backups beyond the newest <paramref name="keep"/>.</summary>
    private static List<string> Rotate(string folder, int keep)
    {
        var old = List(folder).Skip(Math.Max(1, keep)).ToList();
        foreach (var file in old)
            File.Delete(Path.Combine(folder, file.Name));
        return [.. old.Select(f => f.Name)];
    }

    /// <summary>The backups in the schedule's folder, newest first.</summary>
    public async Task<(string Folder, IReadOnlyList<StoredBackup> Files)> FilesAsync(CancellationToken cancellationToken = default)
    {
        var folder = ResolveFolder((await GetAsync(cancellationToken)).Folder);
        try
        {
            return (folder, List(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (folder, []);
        }
    }

    private static List<StoredBackup> List(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return [.. new DirectoryInfo(folder).EnumerateFiles(DataBackup.FilePrefix + "*")
            .Where(f => BackupName().IsMatch(f.Name) && f.LinkTarget is null)
            // The time is in the name, so the order doesn't depend on the file system's.
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new StoredBackup(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Name.EndsWith(BackupCipher.Extension, StringComparison.Ordinal)))];
    }

    /// <summary>Opens a backup in the folder for reading; null when there's no such backup.</summary>
    public async Task<Stream?> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!BackupName().IsMatch(name))
            return null;
        var path = Path.Combine(ResolveFolder((await GetAsync(cancellationToken)).Folder), name);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <returns>False when there's no such backup.</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!BackupName().IsMatch(name))
            return false;
        var path = Path.Combine(ResolveFolder((await GetAsync(cancellationToken)).Folder), name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        string[] keys = [IntervalKey, KeepKey, FolderKey, PassphraseKey];
        return await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
    }

    private static int? Int(Dictionary<string, string> stored, string key) =>
        stored.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^sockless-port-backup-\d{8}-\d{6}\.tar\.gz(\.enc)?$")]
    private static partial Regex BackupName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote a scheduled backup to {Path}")]
    private static partial void LogWritten(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A scheduled backup failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
