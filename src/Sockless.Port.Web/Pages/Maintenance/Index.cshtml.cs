using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Backup;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Maintenance;
using Sockless.Port.Settings;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Maintenance;

/// <summary>One endpoint's scheduled work: its prune and its update check.</summary>
public sealed record EndpointMaintenance(EngineConnection Connection, PruneSchedule? Prune, MaintenanceRun? LastPrune, DateTimeOffset? NextPrune,
    MaintenanceRun? LastUpdateCheck, DateTimeOffset? UpdatesCheckedAt, MaintenanceRun? LastScan);

/// <summary>Every schedule (backups, prunes, update checks, scans) with its last and next run, and the history of runs.</summary>
public sealed class IndexModel(ScheduledBackups backups, PruneSchedules prunes, PruneRunner runner, MaintenanceLog log, EngineConnections connections,
    PortSettings settings, ImageUpdates updates, DataBackup backup, TimeProvider time) : PageModel
{
    public BackupSchedule Backup { get; private set; } = BackupSchedule.Default;

    public string BackupFolder { get; private set; } = "";

    public MaintenanceRun? LastBackup { get; private set; }

    public DateTimeOffset? NextBackup { get; private set; }

    public bool BackupRunning => backups.IsRunning;

    public IReadOnlyList<StoredBackup> StoredBackups { get; private set; } = [];

    public IReadOnlyList<EndpointMaintenance> Endpoints { get; private set; } = [];

    public AppSettings Settings => settings.Current;

    public MaintenanceRunPage History { get; private set; } = new([], 1, false);

    public DateTimeOffset Now { get; private set; }

    [FromQuery(Name = "p")]
    public int PageNumber { get; set; } = 1;

    public bool IsPruning(int endpointId) => runner.IsRunning(endpointId);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Now = time.GetUtcNow();
        Backup = await backups.GetAsync(cancellationToken);
        (BackupFolder, StoredBackups) = await backups.FilesAsync(cancellationToken);
        NextBackup = await backups.NextAsync(Backup, cancellationToken);

        var latest = await log.LatestAsync(cancellationToken);
        LastBackup = latest.GetValueOrDefault((MaintenanceTask.Backup, null));
        var schedules = await prunes.AllAsync(cancellationToken);
        Endpoints = [.. connections.All.Select(c =>
        {
            var prune = schedules.GetValueOrDefault(c.Id);
            var lastPrune = latest.GetValueOrDefault((MaintenanceTask.Prune, c.Id));
            return new EndpointMaintenance(c, prune, lastPrune, prune is null ? null : MaintenanceSchedule.NextPrune(prune, lastPrune, Now),
                latest.GetValueOrDefault((MaintenanceTask.UpdateCheck, c.Id)), updates.LastChecked(c.Id),
                latest.GetValueOrDefault((MaintenanceTask.VulnerabilityScan, c.Id)));
        })];
        History = await log.RecentAsync(PageNumber, cancellationToken);
    }

    /// <summary>Writes a backup to the folder now. The run writes its own audit entry, with the file.</summary>
    public async Task<IActionResult> OnPostBackupNowAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Skip(HttpContext);
        try
        {
            var result = await backups.RunAsync(Trigger(), cancellationToken);
            if (result.Outcome == MaintenanceOutcome.Succeeded)
                Flash.Success(TempData, result.Summary);
            else
                Flash.Error(TempData, result.Summary);
        }
        catch (BackupBusyException ex)
        {
            Flash.Error(TempData, ex.Message);
        }
        return RedirectToPage();
    }

    /// <summary>A stored backup. A post, so it's in the audit log and no link can start one.</summary>
    public async Task<IActionResult> OnPostDownloadAsync(string name, CancellationToken cancellationToken)
    {
        if (await backups.OpenAsync(name, cancellationToken) is not { } stream)
        {
            Flash.Error(TempData, $"There's no backup called {name} any more.");
            return RedirectToPage();
        }
        Response.Headers.CacheControl = "no-store";
        return File(stream, "application/octet-stream", name);
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (await backups.DeleteAsync(name, cancellationToken))
            Flash.Success(TempData, $"Deleted {name}.");
        else
            Flash.Error(TempData, $"There's no backup called {name} any more.");
        return RedirectToPage();
    }

    /// <summary>Stages a stored backup for restoring (with the stored passphrase when it's encrypted); Settings restarts to apply it.</summary>
    public async Task<IActionResult> OnPostRestoreAsync(string name, CancellationToken cancellationToken)
    {
        await using var stream = await backups.OpenAsync(name, cancellationToken);
        if (stream is null)
        {
            Flash.Error(TempData, $"There's no backup called {name} any more.");
            return RedirectToPage();
        }
        try
        {
            string? passphrase = null;
            if (name.EndsWith(BackupCipher.Extension, StringComparison.Ordinal))
            {
                try
                {
                    passphrase = await backups.PassphraseAsync(cancellationToken);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                }
                if (passphrase is null)
                {
                    Flash.Error(TempData, $"{name} is encrypted and no passphrase is stored. Download it and restore it in Settings with its passphrase.");
                    return RedirectToPage();
                }
            }
            var staged = await backup.StageAsync(stream, passphrase, cancellationToken);
            Flash.Success(TempData, $"The backup from {Format.Timestamp(staged.Manifest.CreatedAt)} is ready to restore. Restart Sockless Port to apply it.");
            return RedirectToPage("/Settings/Index");
        }
        catch (BackupRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
            return RedirectToPage();
        }
    }

    /// <summary>Runs an endpoint's prune now. The run writes its own audit entry, with what it removed.</summary>
    public async Task<IActionResult> OnPostPruneNowAsync(int id, CancellationToken cancellationToken)
    {
        if (connections.Get(id) is not { } connection)
            return NotFound();
        if (await prunes.GetAsync(id, cancellationToken) is not { Options.IsEmpty: false } schedule)
        {
            Flash.Error(TempData, $"Choose what to prune on {connection.Name} first.");
            return RedirectToPage();
        }
        if (connection.Client is not { } client)
        {
            Flash.Error(TempData, $"{connection.Name} is offline. Try again once it's back.");
            return RedirectToPage();
        }
        AuditTrail.Skip(HttpContext);
        try
        {
            var result = await runner.RunAsync(connection, client, schedule.Options, Trigger(), cancellationToken);
            if (result.Outcome == MaintenanceOutcome.Succeeded)
                Flash.Success(TempData, $"Pruned {connection.Name}. {result.Summary}");
            else
                Flash.Error(TempData, $"The prune of {connection.Name} didn't fully work. {result.Summary}");
        }
        catch (PruneBusyException ex)
        {
            Flash.Error(TempData, ex.Message);
        }
        return RedirectToPage();
    }

    private MaintenanceTrigger Trigger()
    {
        var access = HttpContext.Access();
        return new MaintenanceTrigger(access?.UserName ?? User.Identity?.Name ?? "anonymous", access?.UserId, HttpContext.Connection.RemoteIpAddress?.ToString());
    }
}
