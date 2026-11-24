using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sockless.Port.Stacks;

/// <summary>A commit, as <c>git log</c> describes it.</summary>
public sealed record GitCommit(string Sha, string Author, DateTimeOffset At, string Subject)
{
    public string Short => GitSource.Short(Sha);
}

/// <summary>What a stack takes from its repository: where, which branch, which files, and how to sign in.</summary>
public sealed record GitCheckoutRequest(GitRemote Remote, string Branch, string ComposePath, string? EnvPath);

/// <summary>A fetch's outcome: the commit checked out and the stack's files read from it.</summary>
/// <param name="Env">The repository's <c>.env</c>; null when the stack keeps its own.</param>
/// <param name="KnownHosts">The SSH host keys after the fetch (learnt on the first connection); null for https.</param>
public sealed record GitFetchResult(GitCommit Commit, string Compose, string? Env, string? KnownHosts);

/// <summary>
/// The git work for git-backed stacks: fetching a branch into the stack's checkout, asking which
/// commit a branch is at (cheap, for polling), and listing the commits between two deploys.
/// </summary>
public sealed class StackGit(GitCli git, ILogger<StackGit> logger)
{
    /// <summary>The longest a fetch (with its checkout) may take.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The longest asking for a branch's commit may take.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(1);

    public GitCli Cli => git;

    /// <summary>
    /// Fetches the branch into <paramref name="checkout"/> (created when missing) and checks out its
    /// newest commit, or <paramref name="commit"/> when given (to rebuild a lost checkout of what
    /// was reviewed). Only the branch is fetched, without tags, submodules or file contents of
    /// older commits; symbolic links are checked out as plain files, so none can point outside.
    /// </summary>
    /// <exception cref="GitException">The fetch failed, or the files aren't in the repository.</exception>
    /// <exception cref="ComposeUnavailableException">Git isn't installed.</exception>
    public async Task<GitFetchResult> FetchAsync(GitCheckoutRequest request, string checkout, string? commit, Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        await using var session = await git.OpenAsync(request.Remote, cancellationToken);
        var branch = request.Branch;
        var tracking = $"refs/remotes/origin/{branch}";
        try
        {
            if (!Directory.Exists(Path.Combine(checkout, ".git")))
            {
                Directory.CreateDirectory(checkout);
                await RunAsync(session, ["-c", "init.defaultBranch=main", "init", "--quiet", checkout], "prepare the checkout", onLine, timeout.Token);
            }
            await RunAsync(session, ["-C", checkout, "config", "remote.origin.url", request.Remote.Url], "prepare the checkout", null, timeout.Token);

            onLine?.Invoke($"$ git fetch {request.Remote.Url} {branch}");
            await RunAsync(session, ["-C", checkout, "fetch", "--no-tags", "--prune", "--no-recurse-submodules", "--filter=blob:none", "--quiet",
                "origin", $"+refs/heads/{branch}:{tracking}"], "fetch", onLine, timeout.Token);

            var target = commit ?? (await RunAsync(session, ["-C", checkout, "rev-parse", "--verify", "--quiet", $"{tracking}^{{commit}}"],
                $"find the branch {branch}", null, timeout.Token)).Stdout.Trim();
            if (!GitSource.IsCommit(target))
                throw new GitException($"The branch {branch} wasn't found in the repository.");
            if (commit is not null)
            {
                var exists = await session.RunAsync(["-C", checkout, "cat-file", "-e", $"{commit}^{{commit}}"], cancellationToken: timeout.Token);
                if (!exists.Succeeded)
                    throw new GitException($"The commit {GitSource.Short(commit)} is no longer on {branch}. Fetch again to take the branch's newest commit.");
            }

            onLine?.Invoke($"$ git checkout {GitSource.Short(target)}");
            await RunAsync(session, ["-C", checkout, "-c", "core.symlinks=false", "-c", "core.autocrlf=false", "-c", "advice.detachedHead=false",
                "checkout", "--quiet", "--force", "--detach", target], "check out the files", onLine, timeout.Token);
            await RunAsync(session, ["-C", checkout, "clean", "-ffdxq"], "check out the files", onLine, timeout.Token);

            var described = await LogAsync(session, checkout, ["-1", target], timeout.Token);
            if (described.Count == 0)
                throw new GitException($"Couldn't read the commit {GitSource.Short(target)}.");
            var compose = ReadFile(checkout, request.ComposePath, "The compose file", branch);
            var env = request.EnvPath is { } envPath ? ReadFile(checkout, envPath, "The .env file", branch) : null;
            onLine?.Invoke($"Checked out {described[0].Short} ({described[0].Subject}).");
            return new GitFetchResult(described[0], compose, env, GitSource.IsSsh(request.Remote.Url) ? session.KnownHosts() : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitException($"The fetch didn't finish within {FetchTimeout.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>The commit the branch is at in the repository, without fetching anything: <c>git ls-remote</c>.</summary>
    /// <returns>The commit, and the host keys after connecting (learnt when there were none).</returns>
    /// <exception cref="GitException">The repository can't be reached, or has no such branch.</exception>
    public async Task<(string Commit, string? KnownHosts)> HeadAsync(GitRemote remote, string branch, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        await using var session = await git.OpenAsync(remote, cancellationToken);
        try
        {
            var result = await RunAsync(session, ["ls-remote", "--", remote.Url, $"refs/heads/{branch}"], "check the branch", null, timeout.Token);
            var line = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.EndsWith($"\trefs/heads/{branch}", StringComparison.Ordinal));
            var sha = line?.Split('\t')[0];
            if (!GitSource.IsCommit(sha))
                throw new GitException($"The branch {branch} wasn't found in the repository.");
            return (sha!, GitSource.IsSsh(remote.Url) ? session.KnownHosts() : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitException($"The repository didn't answer within {CheckTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// The commits after <paramref name="from"/> up to <paramref name="to"/>, newest first, from the
    /// checkout (nothing is fetched). Null when that can't be told, e.g. <paramref name="from"/> is
    /// no longer in the branch's history after a force push.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>?> CommitsAsync(string checkout, string? from, string to, int max, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(checkout, ".git")) || !GitSource.IsCommit(to) || (from is not null && !GitSource.IsCommit(from)))
            return null;
        try
        {
            await using var session = await git.OpenAsync(null, cancellationToken);
            return await LogAsync(session, checkout, ["-n", max.ToString(CultureInfo.InvariantCulture), from is null ? to : $"{from}..{to}"], cancellationToken);
        }
        catch (Exception ex) when (ex is GitException or ComposeUnavailableException or IOException)
        {
            logger.LogDebug(ex, "Couldn't list the commits in {Checkout}", checkout);
            return null;
        }
    }

    private static async Task<IReadOnlyList<GitCommit>> LogAsync(GitSession session, string checkout, IReadOnlyList<string> range, CancellationToken cancellationToken)
    {
        var result = await RunAsync(session, ["-C", checkout, "log", "--no-decorate", "--format=%H%x1f%an%x1f%cI%x1f%s", .. range, "--"],
            "read the history", null, cancellationToken);
        List<GitCommit> commits = [];
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\u001f');
            if (parts.Length == 4 && GitSource.IsCommit(parts[0])
                && DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                commits.Add(new GitCommit(parts[0], parts[1], at, parts[3].Length > StackRepository.MaxSubject ? parts[3][..StackRepository.MaxSubject] : parts[3]));
            }
        }
        return commits;
    }

    /// <summary>Runs one command; a failure becomes a <see cref="GitException"/> naming git's own last error.</summary>
    private static async Task<ComposeResult> RunAsync(GitSession session, IReadOnlyList<string> arguments, string what, Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var result = await session.RunAsync(arguments, onLine, cancellationToken);
        if (!result.Succeeded)
            throw new GitException(GitHints.Describe(what, result.Output, session.UsesSshKey));
        return result;
    }

    /// <summary>A file of the checkout as text: regular, at most <see cref="GitSource.MaxFileBytes"/>, UTF-8.</summary>
    internal static string ReadFile(string checkout, string path, string what, string branch)
    {
        var root = Path.GetFullPath(checkout);
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new GitException($"{what} must be inside the repository.");
        var file = new FileInfo(full);
        if (!file.Exists)
            throw new GitException(Directory.Exists(full)
                ? $"{path} is a folder in the repository. Give the path of {what.ToLowerInvariant()} itself."
                : $"{what} isn't in the repository at {path} on {branch}.");
        if (file.LinkTarget is not null)
            throw new GitException($"{path} is a link. Point at the file itself.");
        if (file.Length > GitSource.MaxFileBytes)
            throw new GitException($"{path} is larger than {GitSource.MaxFileBytes / 1024 / 1024} MiB.");
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(full));
            return StackContent.NormalizeText(text.TrimStart('﻿'));
        }
        catch (DecoderFallbackException)
        {
            throw new GitException($"{path} isn't UTF-8 text.");
        }
    }
}

/// <summary>What a git failure means, in a sentence, with what to do for the common ones.</summary>
public static class GitHints
{
    public static string Describe(string what, IReadOnlyList<string> output, bool sshKey)
    {
        var text = string.Join('\n', output);
        var reason = output.LastOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
                     ?? output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
                     ?? "git gave no reason";
        reason = reason.Replace("fatal: ", "", StringComparison.Ordinal).Replace("error: ", "", StringComparison.Ordinal).Trim();
        var hint = Hint(text, sshKey);
        return $"Couldn't {what}: {Engines.EngineErrors.Sentence(reason)}{(hint is null ? "" : " " + hint)}";
    }

    private static string? Hint(string output, bool sshKey)
    {
        bool Has(string s) => output.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("REMOTE HOST IDENTIFICATION HAS CHANGED") || Has("Host key verification failed"))
            return "The SSH server's host key isn't the one seen before. If the server really changed it, forget the stored host key in the stack's repository settings, then fetch again.";
        if (Has("Permission denied (publickey"))
            return sshKey
                ? "The server refused the key: add its public key to the repository (a deploy key, read-only is enough)."
                : "The repository needs an SSH key: add one under Sign in.";
        if (Has("Authentication failed") || Has("could not read Username") || Has("403") || Has("401"))
            return "Check the token: it may be wrong, expired, or lack read access to the repository's contents.";
        if (Has("Repository not found") || (Has("not found") && Has("repository")))
            return "Check the address. Private repositories also answer \"not found\" without a valid token or key.";
        if (Has("couldn't find remote ref"))
            return "Check the branch's name.";
        if (Has("Could not resolve host") || Has("Name or service not known") || Has("Temporary failure in name resolution"))
            return "The server couldn't look up the host: check the address and the server's DNS.";
        if (Has("transport") && Has("not allowed"))
            return "Only https, http and ssh are allowed.";
        if (Has("Connection timed out") || Has("Connection refused") || Has("Operation timed out"))
            return "The server couldn't connect to the repository's host. A proxy goes in HTTPS_PROXY.";
        return null;
    }
}
