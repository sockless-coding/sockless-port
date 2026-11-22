using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;

namespace Sockless.Port.Audit;

/// <summary>An action to record. <see cref="AuditLog"/> stamps the time.</summary>
public sealed record AuditRecord(
    string? UserId, string UserName, string Action, string? Target = null,
    int? EndpointId = null, string? EndpointName = null,
    AuditOutcome Outcome = AuditOutcome.Succeeded, string? Detail = null, string? RemoteAddress = null);

public sealed record AuditQuery(string? User = null, int? EndpointId = null, AuditOutcome? Outcome = null, string? Search = null, int Page = 1);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Page, bool HasMore);

/// <summary>
/// The audit log: every mutating action, in the database and in the server log (for shipping
/// elsewhere). Writing never fails the action it records.
/// </summary>
public sealed partial class AuditLog(IServiceScopeFactory scopes, TimeProvider time, ILogger<AuditLog> logger)
{
    public const int PageSize = 50;

    internal const int MaxTarget = 1000;
    internal const int MaxDetail = 2000;

    public async Task WriteAsync(AuditRecord record)
    {
        LogAction(logger, record.UserName, record.Action, record.Target ?? "", record.EndpointName ?? "", record.Outcome, record.Detail ?? "");
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            db.AuditEntries.Add(new AuditEntry
            {
                At = time.GetUtcNow(),
                UserId = record.UserId,
                UserName = Truncate(record.UserName, 256)!,
                Action = Truncate(record.Action, 128)!,
                Target = Truncate(record.Target, MaxTarget),
                EndpointId = record.EndpointId,
                EndpointName = Truncate(record.EndpointName, 64),
                Outcome = record.Outcome,
                Detail = Truncate(record.Detail, MaxDetail),
                RemoteAddress = Truncate(record.RemoteAddress, 64),
            });
            // No request token: an action that ran is recorded even if the browser went away.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogWriteFailed(logger, ex);
        }
    }

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var entries = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.User))
            entries = entries.Where(e => e.UserName == query.User);
        if (query.EndpointId is { } endpoint)
            entries = entries.Where(e => e.EndpointId == endpoint);
        if (query.Outcome is { } outcome)
            entries = entries.Where(e => e.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            entries = entries.Where(e => EF.Functions.Like(e.Action, term, "\\") || EF.Functions.Like(e.Target!, term, "\\")
                                         || EF.Functions.Like(e.Detail!, term, "\\") || EF.Functions.Like(e.UserName, term, "\\")
                                         || EF.Functions.Like(e.EndpointName!, term, "\\"));
        }

        var page = Math.Max(1, query.Page);
        var rows = await entries.OrderByDescending(e => e.Id).Skip((page - 1) * PageSize).Take(PageSize + 1).ToListAsync(cancellationToken);
        return new AuditPage([.. rows.Take(PageSize)], page, rows.Count > PageSize);
    }

    /// <summary>The user names that appear in the log, for the filter.</summary>
    public async Task<IReadOnlyList<string>> UserNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.AuditEntries.AsNoTracking().Select(e => e.UserName).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);
    }

    /// <summary>Deletes entries older than <paramref name="retention"/>.</summary>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var cutoff = time.GetUtcNow() - retention;
        // At is stored as a sortable UTC string, so the comparison runs in SQL.
        return await db.AuditEntries.Where(e => e.At < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..(length - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: {User} {Action} {Target} on {Endpoint}: {Outcome} {Detail}")]
    private static partial void LogAction(ILogger logger, string user, string action, string target, string endpoint, AuditOutcome outcome, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't write an audit entry")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);
}
