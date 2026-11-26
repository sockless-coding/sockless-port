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

    /// <summary>
    /// An image reference split for one-line display: the registry and namespace (which tables
    /// shorten first) and the name with its tag, which they keep. <c>ghcr.io/acme/web:1.2</c> is
    /// <c>("ghcr.io/acme/", "web:1.2")</c>; an image ID is shortened to 12 characters.
    /// </summary>
    public static (string Prefix, string Name) SplitReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return ("", "");
        if (reference.StartsWith("sha256:", StringComparison.Ordinal))
            return ("", ImageActions.ShortId(reference));
        // The last slash before any digest: "a/b@sha256:..." keeps the digest with the name.
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        var slash = reference.LastIndexOf('/', at < 0 ? reference.Length - 1 : at);
        return slash < 0 ? ("", reference) : (reference[..(slash + 1)], reference[(slash + 1)..]);
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

    /// <summary>"Checked 3 containers: 1 with an update available, 2 up to date."</summary>
    /// <param name="one">What was checked, singular: "tag in use", "container".</param>
    /// <param name="nothing">What to say when there was nothing to check.</param>
    /// <param name="why">Where the reasons for unknown results are shown: "the table", "their pages".</param>
    public static string CheckMessage(UpdateCheckSummary summary, string one, string many, string nothing, string why)
    {
        if (summary.Checked == 0)
            return nothing;
        var what = $"{summary.Checked} {(summary.Checked == 1 ? one : many)}";
        if (summary.UpToDate == summary.Checked)
            return $"Checked {what}: all up to date.";
        var parts = new List<string>();
        if (summary.Updates > 0)
            parts.Add($"{summary.Updates} with an update available");
        if (summary.UpToDate > 0)
            parts.Add($"{summary.UpToDate} up to date");
        if (summary.Unknown > 0)
            parts.Add($"{summary.Unknown} couldn't be checked ({why} says why)");
        return $"Checked {what}: {string.Join(", ", parts)}.";
    }

    /// <summary>"Last checked 5 minutes ago." for the newest of the results, or that nothing was checked yet.</summary>
    public static string LastChecked(IEnumerable<UpdateStatus?> statuses, string what) =>
        statuses.OfType<UpdateStatus>().Select(s => (DateTimeOffset?)s.CheckedAt).Max() is { } at
            ? $"Last checked {Format.Relative(at)}."
            : $"{what} haven't been checked since Sockless Port started.";
}
