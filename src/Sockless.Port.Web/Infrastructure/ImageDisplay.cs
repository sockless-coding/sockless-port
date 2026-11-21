using Sockless.Port.Docker.Streams;
using Sockless.Port.Images;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>How image jobs and update checks read on the page. <c>images.js</c> mirrors the job texts for live updates.</summary>
public static class ImageDisplay
{
    public static StatusBadge JobBadge(ImageJobView view) => view.Status switch
    {
        ImageJobRunning => new(view.Job.Kind == ImageJobKind.Pull ? "Pulling…" : "Building…", "warning"),
        ImageJobSucceeded => new("Done", "success"),
        ImageJobFailed => new("Failed", "danger"),
        ImageJobCanceled => new("Cancelled", ""),
    };

    /// <summary>"2 of 5 layers · 12.3 MB of 40 MB" while pulling; the outcome once done.</summary>
    public static string JobSummary(ImageJobView view) => view.Status switch
    {
        ImageJobRunning when view.Job.Kind == ImageJobKind.Build => "Building…",
        ImageJobRunning => PullProgress(view.Pull),
        ImageJobSucceeded s => s.Message,
        ImageJobFailed f => f.Error,
        ImageJobCanceled => "Cancelled.",
    };

    public static string PullProgress(PullSnapshot pull)
    {
        if (pull.Layers.Count == 0)
            return pull.Messages.LastOrDefault() ?? "Asking the registry…";
        var layers = $"{pull.LayersDone} of {pull.Layers.Count} layers";
        return pull.TotalBytes > 0 ? $"{layers} · {Format.Bytes(pull.DownloadedBytes)} of {Format.Bytes(pull.TotalBytes)}" : layers;
    }

    /// <summary>A word for the update check, coloured only when it asks for attention.</summary>
    public static StatusBadge? UpdateBadge(UpdateStatus? status) => status switch
    {
        null => null,
        UpToDate => new("Up to date", "success"),
        UpdateAvailable => new("Update available", "warning"),
        UpdateUnknown => new("Can't check", ""),
    };

    public static string? UpdateDetail(UpdateStatus? status) => status switch
    {
        null => null,
        UpToDate u => $"Matches the registry, checked {Format.Relative(u.CheckedAt)}.",
        UpdateAvailable a => $"The registry has a newer image ({ImageActions.ShortId(a.RemoteDigest)}), checked {Format.Relative(a.CheckedAt)}.",
        UpdateUnknown n => n.Reason,
    };
}
