using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Data;

namespace Sockless.Port.Core.Tests;

public sealed class ReplacedMigrationsTests
{
    [Fact]
    public async Task A_database_from_the_unreleased_image_scans_migration_still_migrates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();

        // What 20261122185418_AddImageScans left behind.
        await db.Database.GetService<IMigrator>().MigrateAsync("20261122173954_AddAccessAuditSettings", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"ImageScans\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ImageScans\" PRIMARY KEY AUTOINCREMENT, \"ImageId\" TEXT NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20261122185418_AddImageScans', '11.0.0')", ct);

        await ReplacedMigrations.ApplyAsync(db, ct);
        await db.Database.MigrateAsync(ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        Assert.DoesNotContain("20261122185418_AddImageScans", await db.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(0, await db.ImageScans.CountAsync(ct));
    }

    [Fact]
    public async Task A_new_database_needs_no_repair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();

        await ReplacedMigrations.ApplyAsync(db, ct);
        await db.Database.MigrateAsync(ct);
        await ReplacedMigrations.ApplyAsync(db, ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
    }
}
