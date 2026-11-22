using System.Globalization;

namespace Sockless.Port.Volumes;

public enum VolumeEntryKind
{
    Directory,
    File,
    Link,

    /// <summary>A socket, FIFO or device node.</summary>
    Other,
}

/// <summary>One item in a volume directory.</summary>
/// <param name="Path">From the volume's root, e.g. <c>/conf/app.ini</c>.</param>
/// <param name="Mode">Permission bits in octal, e.g. <c>644</c>.</param>
public sealed record VolumeEntry(string Name, string Path, VolumeEntryKind Kind, long Size, DateTimeOffset Modified, string Mode, string Owner, string Group);

/// <param name="Path">The directory listed, from the volume's root.</param>
/// <param name="Self">The directory itself, or the file or link when <see cref="Path"/> isn't a directory.</param>
/// <param name="Entries">Directories first, then by name. Empty when <see cref="Self"/> isn't a directory.</param>
/// <param name="Truncated">The directory has more entries than were read.</param>
/// <param name="Warning">What the engine printed when some entries couldn't be read (permissions).</param>
public sealed record VolumeListing(string Path, VolumeEntry Self, IReadOnlyList<VolumeEntry> Entries, bool Truncated, string? Warning);

/// <summary>A browse request that can't be answered, with a message for the page.</summary>
public class VolumeBrowseException(string message) : Exception(message);

/// <summary>No helper is open for the volume (never opened, idle, or expired): open it again with a POST.</summary>
public sealed class VolumeBrowserClosedException() : VolumeBrowseException("The file browser closed after a while without use. Open it again to go on.");

public static class VolumePath
{
    /// <summary>
    /// A path inside the volume as <c>/</c> or <c>/a/b</c>, or null when it leaves the volume
    /// (<c>..</c>) or isn't a path. Empty segments and <c>.</c> are dropped.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        if (path.Contains('\0', StringComparison.Ordinal) || path.Contains('\n', StringComparison.Ordinal) || path.Length > 4096)
            return null;
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
                return null;
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>The parent of a normalized path; null for the root.</summary>
    public static string? Parent(string path) =>
        path == "/" ? null : path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    /// <summary>The last segment of a normalized path; empty for the root.</summary>
    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Combine(string directory, string name) => directory == "/" ? "/" + name : $"{directory}/{name}";

    /// <summary>Each ancestor with its name, from the root down, for breadcrumbs.</summary>
    public static IReadOnlyList<(string Name, string Path)> Ancestors(string path)
    {
        var result = new List<(string, string)>();
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            result.Add((segment, current));
        }
        return result;
    }
}

/// <summary>Reads <c>stat -c '%F|%s|%Y|%a|%U|%G|%n'</c> output, one line per entry.</summary>
public static class StatOutput
{
    public const string Format = "%F|%s|%Y|%a|%U|%G|%n";

    /// <param name="mountPath">Where the volume is mounted in the helper, stripped from names.</param>
    public static IEnumerable<VolumeEntry> Parse(string output, string mountPath)
    {
        foreach (var line in output.Split('\n'))
        {
            // The name is last, so a '|' in it survives: split into at most 7 fields.
            var fields = line.Split('|', 7);
            if (fields.Length < 7
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mtime)
                || !fields[6].StartsWith(mountPath, StringComparison.Ordinal))
                continue;

            var inside = fields[6][mountPath.Length..].TrimEnd('/');
            var path = inside.Length == 0 ? "/" : inside;
            yield return new VolumeEntry(VolumePath.Name(path), path, Kind(fields[0]), size,
                DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(mtime, -62135596800, 253402300799)), fields[3], fields[4], fields[5]);
        }
    }

    /// <summary>busybox and GNU stat both describe the type in words.</summary>
    private static VolumeEntryKind Kind(string type) => type switch
    {
        "directory" => VolumeEntryKind.Directory,
        "regular file" or "regular empty file" => VolumeEntryKind.File,
        "symbolic link" => VolumeEntryKind.Link,
        _ => VolumeEntryKind.Other,
    };
}
