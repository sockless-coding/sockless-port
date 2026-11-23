using Microsoft.EntityFrameworkCore;

namespace Sockless.Port.Data;

/// <summary>
/// Undoes migrations that were replaced before any release, so a database started on a build
/// that had them can still migrate. Runs before <c>MigrateAsync</c>.
/// </summary>
public static class ReplacedMigrations
{
    /// <summary>
    /// Created <c>ImageScans</c>; replaced by <c>20261122192926_AddVulnerabilityScans</c>, which
    /// creates it again with the findings and accepted risks. Dropping the table loses only stored
    /// scan results, which the next scan brings back.
    /// </summary>
    internal const string AddImageScans = "20261122185418_AddImageScans";

    public static async Task ApplyAsync(PortDbContext db, CancellationToken cancellationToken = default)
    {
        var history = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'")
            .SingleAsync(cancellationToken);
        if (history == 0)
            return;

        var applied = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {0}", AddImageScans)
            .SingleAsync(cancellationToken);
        if (applied == 0)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ImageScans\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {0}", [AddImageScans], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
