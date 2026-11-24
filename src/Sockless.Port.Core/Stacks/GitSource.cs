using System.Text.RegularExpressions;

namespace Sockless.Port.Stacks;

/// <summary>
/// The parts of a git-backed stack's source people type: the repository's address, the branch and
/// paths in it. Each <c>…Problem</c> says why a value can't be used, or null when it can.
/// </summary>
public static partial class GitSource
{
    public const string DefaultBranch = "main";
    public const string DefaultComposePath = "compose.yaml";

    /// <summary>The largest compose file or <c>.env</c> read from a repository.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    public static string? UrlProblem(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url))
            return "Enter the repository's address.";
        if (url.Length > StackRepository.MaxUrl)
            return $"Use at most {StackRepository.MaxUrl} characters.";
        if (url.StartsWith('-') || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return "The address can't start with - or contain spaces.";

        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "That isn't a valid address.";
            if (uri.Scheme is not ("https" or "http" or "ssh"))
                return "Use an https://, http:// or ssh:// address. Other transports (file://, git://, ext::) aren't allowed.";
            if (uri.Host.Length == 0)
                return "That isn't a valid address: it names no host.";
            if (uri.AbsolutePath is "" or "/")
                return "Add the repository's path, e.g. https://github.com/owner/repo.git.";
            if (uri.Scheme is "https" or "http" && uri.UserInfo.Length > 0)
                return "Leave the user name and token out of the address: give the token under Sign in, where it's stored encrypted.";
            if (uri.Scheme == "ssh" && uri.UserInfo.Contains(':', StringComparison.Ordinal))
                return "Leave the password out of the address: sign in with an SSH key.";
            return null;
        }

        return ScpLike().IsMatch(url)
            ? null
            : "Enter an https:// or ssh:// address, or the scp form git@host:owner/repo.git.";
    }

    /// <summary>The repository is reached over SSH: <c>ssh://</c> or <c>user@host:path</c>.</summary>
    public static bool IsSsh(string url) =>
        url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || (!url.Contains("://", StringComparison.Ordinal) && ScpLike().IsMatch(url));

    /// <summary><c>https://host[:port]/</c> for an http(s) address; null for SSH.</summary>
    public static string? HttpOrigin(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : null;

    /// <summary>The host the address names, lower-case; null when it can't be read.</summary>
    public static string? Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (url.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
        return ScpLike().Match(url) is { Success: true } match ? match.Groups["host"].Value.Trim('[', ']').ToLowerInvariant() : null;
    }

    /// <summary>
    /// A page for the commit on the repository's host, for addresses that are web pages too
    /// (http(s), and SSH on the big public hosts); null when there's no telling.
    /// </summary>
    public static string? CommitLink(string url, string commit)
    {
        string? web = null;
        if (HttpOrigin(url) is not null)
            web = url;
        else if (Host(url) is "github.com" or "gitlab.com" or "codeberg.org" or "gitea.com")
        {
            var path = url.Contains("://", StringComparison.Ordinal)
                ? new Uri(url).AbsolutePath
                : "/" + ScpLike().Match(url).Groups["path"].Value.TrimStart('/');
            web = $"https://{Host(url)}{path}";
        }
        if (web is null || !CommitPattern().IsMatch(commit))
            return null;
        web = web.TrimEnd('/');
        if (web.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            web = web[..^4];
        return $"{web}/commit/{commit}";
    }

    public static string? BranchProblem(string? branch)
    {
        branch = branch?.Trim();
        if (string.IsNullOrEmpty(branch))
            return "Enter the branch, e.g. main.";
        if (branch.Length > StackRepository.MaxBranch)
            return $"Use at most {StackRepository.MaxBranch} characters.";
        if (branch.StartsWith("refs/", StringComparison.Ordinal))
            return "Give the branch's name without refs/heads/.";
        return BranchPattern().IsMatch(branch) && !branch.StartsWith('-') && !branch.EndsWith('/') && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("//", StringComparison.Ordinal)
               && !branch.EndsWith(".lock", StringComparison.Ordinal) && !branch.Split('/').Any(p => p.StartsWith('.'))
            ? null
            : "That isn't a branch name git accepts: use letters, digits, ., _, - and /.";
    }

    /// <summary><c>./deploy\compose.yml</c> → <c>deploy/compose.yml</c>; null when empty.</summary>
    public static string? NormalizePath(string? path)
    {
        path = path?.Trim().Replace('\\', '/');
        while (path is not null && path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <param name="path">Already normalised (<see cref="NormalizePath"/>).</param>
    /// <param name="what">"The compose file", ".env".</param>
    public static string? PathProblem(string? path, string what)
    {
        if (path is null)
            return $"Enter where {what} is in the repository.";
        if (path.Length > StackRepository.MaxPath)
            return $"Use at most {StackRepository.MaxPath} characters.";
        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
            return $"Give the path of {what} from the repository's root, e.g. deploy/compose.yaml.";
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or ".."))
            return "The path can't contain empty parts, . or ..: give it from the repository's root.";
        if (parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
            return "That's inside git's own folder, not a file in the repository.";
        return null;
    }

    /// <summary>The first 7 characters of a commit, as git shows it.</summary>
    public static string Short(string? commit) => commit is null ? "" : commit[..Math.Min(7, commit.Length)];

    public static bool IsCommit(string? value) => value is not null && CommitPattern().IsMatch(value);

    [GeneratedRegex(@"^(?:[A-Za-z0-9._~-]+@)?(?<host>[A-Za-z0-9][A-Za-z0-9.-]+|\[[0-9A-Fa-f:.]+\]):(?<path>[^\s:][^\s]*)$")]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitPattern();
}
