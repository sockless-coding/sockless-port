namespace Sockless.Port.Templates;

/// <summary>
/// Turns a template's repository and file path into a URL the file can be downloaded from.
/// Portainer clones the repository; Sockless Port has no git client, so it reads the one file
/// from the host's raw-file address, on the repository's default branch (<c>HEAD</c>).
/// </summary>
public static class TemplateRepositories
{
    public const string SupportedHosts = "GitHub and GitLab";

    /// <returns>The file's raw URL, or null when the host isn't supported or the path is unsafe.</returns>
    public static Uri? RawUrl(string? repository, string? path)
    {
        if (!Uri.TryCreate(repository?.Trim(), UriKind.Absolute, out var repo) || repo.Scheme != Uri.UriSchemeHttps)
            return null;
        var file = Path(path);
        if (file is null)
            return null;

        var segments = repo.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            segments[^1] = segments[^1][..^4];
        if (segments.Length < 2 || segments.Any(s => s is "." or ".." || s.Contains('%')))
            return null;

        return repo.Host.ToLowerInvariant() switch
        {
            "github.com" or "www.github.com" when segments.Length == 2 =>
                new Uri($"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/HEAD/{file}"),
            "gitlab.com" or "www.gitlab.com" => new Uri($"https://gitlab.com/{string.Join('/', segments)}/-/raw/HEAD/{file}"),
            _ => null,
        };
    }

    /// <summary>A relative path without <c>..</c>, with its segments escaped for a URL.</summary>
    private static string? Path(string? path)
    {
        var segments = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            return null;
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }
}
