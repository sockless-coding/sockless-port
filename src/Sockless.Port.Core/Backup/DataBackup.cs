using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Backup;

/// <summary>The first entry of every backup.</summary>
public sealed record BackupManifest(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("serverVersion")] string? ServerVersion);

/// <summary>An uploaded backup, checked and waiting for the restart that applies it.</summary>
public sealed record StagedRestore(BackupManifest Manifest, int Files, long Bytes);

/// <summary>The upload isn't a backup this server can restore. The message says why.</summary>
public sealed class BackupRejectedException(string message) : Exception(message);

/// <summary>
/// Backs up and restores the data volume: the database (a consistent copy, taken while the server
/// runs), the Data Protection keys, the internal CA and the stack files. Temporary build contexts
/// aren't included. A restore is checked, staged in <see cref="PortOptions.RestorePendingPath"/>,
/// and applied at the next start, before the database opens; what it replaces is kept in a
/// <c>before-restore-*</c> folder.
/// </summary>
public sealed partial class DataBackup(IOptions<PortOptions> options, IServiceScopeFactory scopes, TimeProvider time, ILogger<DataBackup> logger)
{
    public const int FormatVersion = 1;
    public const string ManifestName = "sockless-port-backup.json";
    private const string DatabaseName = "port.db";

    /// <summary>Most a restore may unpack.</summary>
    internal const long MaxRestoreBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Top-level folders a backup holds besides the database.</summary>
    internal static readonly string[] Folders = ["keys", "pki", "stacks"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private PortOptions Port => options.Value;

    public string FileName() => $"sockless-port-backup-{time.GetUtcNow():yyyyMMdd-HHmmss}.tar.gz";

    /// <summary>Writes a gzipped tar of the data volume to <paramref name="output"/>.</summary>
    public async Task WriteAsync(Stream output, string? serverVersion, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Port.TempPath);
        var snapshot = Path.Combine(Port.TempPath, $"backup-{Guid.NewGuid():n}.db");
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
                // A consistent copy while the server keeps writing.
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {snapshot}", cancellationToken);
            }

            await using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

            var manifest = new BackupManifest(FormatVersion, time.GetUtcNow(), serverVersion);
            var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, ManifestName)
            {
                DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)),
            };
            await tar.WriteEntryAsync(manifestEntry, cancellationToken);
            await tar.WriteEntryAsync(snapshot, DatabaseName, cancellationToken);

            foreach (var folder in Folders)
            {
                var root = Path.Combine(Port.DataPath, folder);
                if (!Directory.Exists(root))
                    continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    if (info.LinkTarget is not null)
                        continue;
                    var name = folder + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                    await tar.WriteEntryAsync(file, name, cancellationToken);
                }
            }
        }
        finally
        {
            TryDelete(snapshot);
        }
        LogBackedUp(logger);
    }

    /// <summary>
    /// Unpacks an uploaded backup into the pending-restore folder after checking it: a manifest
    /// this server understands, a database whose migrations it knows, and only regular files
    /// under the expected folders.
    /// </summary>
    /// <exception cref="BackupRejectedException">It isn't a backup this server can restore.</exception>
    public async Task<StagedRestore> StageAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        var staging = Port.RestorePendingPath + ".partial";
        DeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        try
        {
            BackupManifest? manifest = null;
            var files = 0;
            long bytes = 0;
            await using (var gzip = new GZipStream(upload, CompressionMode.Decompress, leaveOpen: true))
            await using (var tar = new TarReader(gzip))
            {
                try
                {
                    while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                    {
                        if (entry.EntryType is TarEntryType.Directory)
                            continue;
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                            throw new BackupRejectedException($"The backup holds {entry.Name}, which isn't a regular file.");

                        var name = entry.Name.Replace('\\', '/').TrimStart('.', '/');
                        if (name == ManifestName)
                        {
                            manifest = await ReadManifestAsync(entry, cancellationToken);
                            continue;
                        }
                        if (!IsAllowed(name))
                            throw new BackupRejectedException($"The backup holds {entry.Name}, which a Sockless Port backup doesn't have.");

                        bytes += entry.Length;
                        if (bytes > MaxRestoreBytes)
                            throw new BackupRejectedException("The backup unpacks to more than 4 GB.");
                        var target = Path.GetFullPath(Path.Combine(staging, name));
                        if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            throw new BackupRejectedException($"The backup holds {entry.Name}, which points outside its folder.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await entry.ExtractToFileAsync(target, overwrite: true, cancellationToken);
                        files++;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
                {
                    throw new BackupRejectedException($"That isn't a Sockless Port backup (a .tar.gz from Settings): {ex.Message}");
                }
            }

            if (manifest is null)
                throw new BackupRejectedException("That isn't a Sockless Port backup: it has no manifest.");
            if (manifest.Format > FormatVersion)
                throw new BackupRejectedException("The backup is from a newer Sockless Port. Update this server first.");
            var database = Path.Combine(staging, DatabaseName);
            if (!File.Exists(database))
                throw new BackupRejectedException("The backup has no database.");
            await CheckMigrationsAsync(database, cancellationToken);

            DeleteDirectory(Port.RestorePendingPath);
            Directory.Move(staging, Port.RestorePendingPath);
            LogStaged(logger, manifest.CreatedAt, files);
            return new StagedRestore(manifest, files, bytes);
        }
        catch
        {
            DeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>Is a restore waiting for the next start?</summary>
    public bool IsRestorePending => Directory.Exists(Port.RestorePendingPath);

    public void CancelPendingRestore() => DeleteDirectory(Port.RestorePendingPath);

    /// <summary>
    /// Applies a staged restore. Runs at start, before anything opens the database: what's there
    /// moves to <c>before-restore-{time}</c>, then the backup's files take its place.
    /// </summary>
    /// <returns>The folder the replaced data went to, or null when no restore was pending.</returns>
    public static string? ApplyPending(PortOptions port, DateTimeOffset now)
    {
        var pending = port.RestorePendingPath;
        if (!Directory.Exists(pending))
            return null;

        var aside = Path.Combine(port.DataPath, $"before-restore-{now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(aside);
        SqliteConnection.ClearAllPools();
        foreach (var name in new[] { DatabaseName, DatabaseName + "-wal", DatabaseName + "-shm" })
        {
            var path = Path.Combine(port.DataPath, name);
            if (File.Exists(path))
                File.Move(path, Path.Combine(aside, name));
        }
        foreach (var folder in Folders)
        {
            var path = Path.Combine(port.DataPath, folder);
            if (Directory.Exists(path))
                Directory.Move(path, Path.Combine(aside, folder));
        }

        File.Move(Path.Combine(pending, DatabaseName), port.DatabasePath);
        foreach (var folder in Folders)
        {
            var path = Path.Combine(pending, folder);
            if (Directory.Exists(path))
                Directory.Move(path, Path.Combine(port.DataPath, folder));
        }
        DeleteDirectory(pending);
        return aside;
    }

    internal static bool IsAllowed(string name) =>
        name == DatabaseName || (Folders.Any(f => name.StartsWith(f + "/", StringComparison.Ordinal)) && !name.Split('/').Any(part => part is "" or "." or ".."));

    private static async Task<BackupManifest> ReadManifestAsync(TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.DataStream is null || entry.Length > 64 * 1024)
            throw new BackupRejectedException("The backup's manifest isn't valid.");
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(entry.DataStream, Json, cancellationToken)
                   ?? throw new BackupRejectedException("The backup's manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupRejectedException($"The backup's manifest isn't valid: {ex.Message}");
        }
    }

    /// <summary>The backup's database must have no migration this server doesn't know (it would be from a newer version).</summary>
    private async Task CheckMigrationsAsync(string database, CancellationToken cancellationToken)
    {
        string[] known;
        await using (var scope = scopes.CreateAsyncScope())
            known = [.. scope.ServiceProvider.GetRequiredService<PortDbContext>().Database.GetMigrations()];

        var applied = new List<string>();
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                applied.Add(reader.GetString(0));
        }
        catch (SqliteException ex)
        {
            throw new BackupRejectedException($"The backup's database can't be read: {ex.Message}");
        }

        if (applied.Except(known, StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
            throw new BackupRejectedException($"The backup's database is from a newer Sockless Port (it has {unknown[0]}). Update this server first.");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote a backup of the data volume")]
    private static partial void LogBackedUp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Staged a restore of the backup from {CreatedAt} ({Files} files). It's applied at the next start.")]
    private static partial void LogStaged(ILogger logger, DateTimeOffset createdAt, int files);
}
