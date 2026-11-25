using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;

namespace Sockless.Port.Maintenance;

/// <summary>Who started a run: the schedule, or a user from a page.</summary>
public sealed record MaintenanceTrigger(string By, string? UserId = null, string? RemoteAddress = null)
{
    public static MaintenanceTrigger Schedule { get; } = new("schedule");

    public bool IsSchedule => UserId is null && By == Schedule.By;
}

public sealed record MaintenanceRunPage(IReadOnlyList<MaintenanceRun> Runs, int Page, bool HasMore);

/// <summary>
/// The history of maintenance runs: backups, prunes, and the scheduled update checks and scans.
/// Kept as long as the audit log. Writing never fails the work it records.
/// </summary>
public sealed partial class MaintenanceLog(IServiceScopeFactory scopes, ILogger<MaintenanceLog> logger)
{
    public const int PageSize = 50;

    public async Task RecordAsync(MaintenanceRun run)
    {
        if (run.Summary.Length > MaintenanceRun.MaxSummary)
            run.Summary = run.Summary[..(MaintenanceRun.MaxSummary - 1)] + "…";
        if (run.By.Length > 256)
            run.By = run.By[..256];
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            db.MaintenanceRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogWriteFailed(logger, ex);
        }
    }

    /// <summary>The newest run of each task and endpoint.</summary>
    public async Task<IReadOnlyDictionary<(MaintenanceTask Task, int? EndpointId), MaintenanceRun>> LatestAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var ids = await db.MaintenanceRuns.AsNoTracking()
            .GroupBy(r => new { r.Task, r.EndpointId })
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(cancellationToken);
        var runs = await db.MaintenanceRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        return runs.ToDictionary(r => (r.Task, r.EndpointId));
    }

    /// <summary>The newest successful run of <paramref name="task"/>.</summary>
    public async Task<MaintenanceRun?> LastSucceededAsync(MaintenanceTask task, int? endpointId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.MaintenanceRuns.AsNoTracking()
            .Where(r => r.Task == task && r.EndpointId == endpointId && r.Outcome == MaintenanceOutcome.Succeeded)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<MaintenanceRunPage> RecentAsync(int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var runs = await db.MaintenanceRuns.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .ToListAsync(cancellationToken);
        return new MaintenanceRunPage([.. runs.Take(PageSize)], page, runs.Count > PageSize);
    }

    /// <summary>Deletes runs older than <paramref name="keep"/>.</summary>
    public async Task<int> PruneAsync(TimeSpan keep, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        // Sortable UTC strings compare in time order.
        var cutoff = now - keep;
        return await db.MaintenanceRuns.Where(r => r.StartedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record a maintenance run")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex);
}
