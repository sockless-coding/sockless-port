using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Data;

namespace Sockless.Port.Maintenance;

public sealed record PruneScheduleError(string Field, string Message);

/// <summary>The scheduled prunes, one per endpoint at most (removed with the endpoint).</summary>
public sealed class PruneSchedules(IServiceScopeFactory scopes, TimeProvider time)
{
    public async Task<IReadOnlyDictionary<int, PruneSchedule>> AllAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.PruneSchedules.AsNoTracking().ToDictionaryAsync(s => s.EndpointId, cancellationToken);
    }

    public async Task<PruneSchedule?> GetAsync(int endpointId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.PruneSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.EndpointId == endpointId, cancellationToken);
    }

    public static IReadOnlyList<PruneScheduleError> Problems(int intervalHours, PruneOptions options)
    {
        List<PruneScheduleError> errors = [];
        if (!PruneOptions.IntervalChoices.Contains(intervalHours))
            errors.Add(new("IntervalHours", "Choose how often from the list."));
        if (!Enum.IsDefined(options.Images))
            errors.Add(new("Images", "Choose which images from the list."));
        if (!Enum.IsDefined(options.Volumes))
            errors.Add(new("Volumes", "Choose which volumes from the list."));
        if (options.OlderThanDays is < 0 or > PruneOptions.MaxOlderThanDays)
            errors.Add(new("OlderThanDays", $"Use 0 (any age) to {PruneOptions.MaxOlderThanDays} days."));
        if (intervalHours > 0 && options.IsEmpty && errors.Count == 0)
            errors.Add(new("Images", "Choose something to remove, or turn the schedule off."));
        return errors;
    }

    /// <returns>What's wrong; nothing was saved then.</returns>
    public async Task<IReadOnlyList<PruneScheduleError>> SaveAsync(int endpointId, int intervalHours, PruneOptions options, CancellationToken cancellationToken = default)
    {
        if (Problems(intervalHours, options) is { Count: > 0 } problems)
            return problems;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId, cancellationToken))
            return [new("", "That endpoint was removed.")];
        var schedule = await db.PruneSchedules.SingleOrDefaultAsync(s => s.EndpointId == endpointId, cancellationToken);
        if (schedule is null)
        {
            schedule = new PruneSchedule { EndpointId = endpointId };
            db.PruneSchedules.Add(schedule);
        }
        schedule.IntervalHours = intervalHours;
        schedule.Images = options.Images;
        schedule.OlderThanDays = options.OlderThanDays;
        schedule.Volumes = options.Volumes;
        schedule.Networks = options.Networks;
        schedule.BuildCache = options.BuildCache;
        schedule.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return [];
    }
}
