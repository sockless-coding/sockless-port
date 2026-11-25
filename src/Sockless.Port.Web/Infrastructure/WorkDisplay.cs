using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Work;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>How background work (<see cref="WorkTracker"/>) is shown: who sees it, its icon, badge and link.</summary>
public static class WorkDisplay
{
    /// <summary>Rows the top bar's list shows before "Show all".</summary>
    public const int TrayRows = 8;

    /// <summary>
    /// The work the user may see: on endpoints they have a role on. Work that isn't about an
    /// endpoint (backups) and prunes (part of maintenance) are for administrators.
    /// </summary>
    public static IReadOnlyList<WorkView> VisibleTo(UserAccess? access, IEnumerable<WorkView> work) => access is null
        ? []
        : [.. work.Where(w => access.IsAdmin || w.Kind != WorkKind.Prune && w.EndpointId is { } id && access.CanView(id))];

    public static string Icon(WorkKind kind) => kind switch
    {
        WorkKind.Pull => "cloud-arrow-down",
        WorkKind.Build => "wrench-screwdriver",
        WorkKind.Stack => "rectangle-stack",
        WorkKind.Scan => "shield-check",
        WorkKind.UpdateCheck => "arrow-up-circle",
        WorkKind.RemoveImages => "trash",
        WorkKind.PruneImages or WorkKind.Prune => "trash",
        WorkKind.Backup => "arrow-down-tray",
        _ => "clipboard-document-list",
    };

    /// <summary>Always a word: status is never colour alone.</summary>
    public static StatusBadge Badge(WorkState state) => state switch
    {
        WorkQueued => new("Queued", ""),
        WorkRunning => new("Running", "info"),
        WorkSucceeded => new("Done", "success"),
        WorkFailed => new("Failed", "danger"),
        WorkCanceled => new("Cancelled", "warning"),
    };

    /// <summary>The outcome, or what it's doing now.</summary>
    public static string? Message(WorkView work) => work.State switch
    {
        WorkSucceeded s => s.Message,
        WorkFailed f => f.Error,
        WorkCanceled => "Cancelled.",
        WorkQueued => "Waiting for the scans before it.",
        WorkRunning => work.Detail,
    };

    /// <summary>"12.5 MB of 40 MB", "3 of 8".</summary>
    public static string? ProgressText(WorkProgress? progress) => progress switch
    {
        null => null,
        { Unit: WorkUnit.Bytes } p => $"{Format.Bytes(p.Done)} of {Format.Bytes(p.Total)}",
        var p => $"{p.Done} of {p.Total}",
    };

    /// <summary>Where to see more: the page the work is about, if the user may open it.</summary>
    public static string? Link(WorkView work, IUrlHelper url, UserAccess? access)
    {
        if (work.EndpointId is not { } endpointId)
            return access?.IsAdmin == true ? url.Page("/Maintenance/Index") : null;
        return work.Kind switch
        {
            WorkKind.Build when access?.Can(endpointId, EndpointPermission.Operate) == true && work.Subject is { } job =>
                url.Page("/Engine/Images/Build", new { endpointId, job }),
            WorkKind.Stack when work.Subject is { } stack => url.Page("/Engine/Stacks/Detail", new { endpointId, stackName = stack }),
            WorkKind.Scan when work.Subject is { } imageId => url.Page("/Engine/Images/Vulnerabilities", new { endpointId, imageId = ImagePageModel.RouteId(imageId) }),
            WorkKind.Prune => access?.IsAdmin == true ? url.Page("/Maintenance/Index") : null,
            _ => url.Page("/Engine/Images/Index", new { endpointId }),
        };
    }
}
