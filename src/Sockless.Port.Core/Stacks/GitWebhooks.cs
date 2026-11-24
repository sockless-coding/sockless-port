using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sockless.Port.Stacks;

/// <summary>What a verified webhook call asks for. Switch on it exhaustively.</summary>
public closed record WebhookIntent;

/// <summary>The host checks that the hook works (GitHub's <c>ping</c>): answer, do nothing.</summary>
public sealed record WebhookPing : WebhookIntent;

/// <summary>Commits were pushed to the stack's branch, or the caller didn't say what happened: fetch and deploy.</summary>
/// <param name="Commit">The commit the branch now points at, when the payload says.</param>
public sealed record WebhookPush(string? Commit) : WebhookIntent;

/// <summary>Something that doesn't concern the stack: another branch, a tag, an issue.</summary>
public sealed record WebhookIgnored(string Reason) : WebhookIntent;

/// <summary>
/// Webhook calls that ask a git-backed stack to redeploy. Each stack has its own address and
/// secret. The secret is checked the way the caller's host sends it: GitHub (and Gitea, Forgejo)
/// sign the body with HMAC-SHA256 in <c>X-Hub-Signature-256</c>; Gitea, Gogs and Forgejo also in
/// their own header; GitLab sends the secret itself in <c>X-Gitlab-Token</c>, which other callers
/// (a CI job, curl) can use too.
/// </summary>
public static class GitWebhooks
{
    /// <summary>The largest body read. Push payloads are far smaller; GitHub's limit is 25 MB, but only for huge pushes.</summary>
    public const int MaxBody = 5 * 1024 * 1024;

    /// <summary>A new secret: 32 random bytes as hex.</summary>
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>A new address part: 16 random bytes as hex, so stacks' hooks can't be guessed or enumerated.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether the call carries the secret in one of the forms the hosts use. Comparisons take constant time.</summary>
    /// <param name="header">Reads a request header; null when absent.</param>
    /// <returns>The host whose form matched ("GitHub", "Gitea", "GitLab"), or null when none did.</returns>
    public static string? Verify(Func<string, string?> header, ReadOnlySpan<byte> body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var mac = HMACSHA256.HashData(key, body);

        if (header("X-Hub-Signature-256") is { } hub)
            return hub.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) && HexEquals(hub[7..], mac) ? Host(header) : null;
        foreach (var name in (string[])["X-Gitea-Signature", "X-Forgejo-Signature", "X-Gogs-Signature"])
        {
            if (header(name) is { } signature)
                return HexEquals(signature, mac) ? "Gitea" : null;
        }
        if (header("X-Gitlab-Token") is { } token)
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(key)) ? "GitLab" : null;
        return null;
    }

    /// <summary>What the call is about, read from the event header and the payload's <c>ref</c>.</summary>
    public static WebhookIntent Read(Func<string, string?> header, ReadOnlySpan<byte> body, string branch)
    {
        var github = header("X-GitHub-Event");
        var gitea = header("X-Gitea-Event") ?? header("X-Forgejo-Event") ?? header("X-Gogs-Event");
        var gitlab = header("X-Gitlab-Event");
        if (github == "ping")
            return new WebhookPing();
        if (github is not null && github != "push")
            return new WebhookIgnored($"GitHub sent a {github} event; only pushes redeploy.");
        if (gitea is not null && gitea != "push")
            return new WebhookIgnored($"The {gitea} event doesn't redeploy; only pushes do.");
        if (gitlab is not null && gitlab != "Push Hook")
            return new WebhookIgnored($"GitLab sent a {gitlab}; only pushes to a branch redeploy.");

        var (reference, after) = Payload(body);
        if (reference is null)
        {
            // A known host's push always names its ref. Other callers (curl, CI) needn't.
            return github is null && gitea is null && gitlab is null
                ? new WebhookPush(null)
                : new WebhookIgnored("The push didn't say which branch it was for.");
        }
        if (reference != $"refs/heads/{branch}")
            return new WebhookIgnored($"The push was to {Short(reference)}, and the stack follows {branch}.");
        // Deleting the branch pushes all zeros.
        if (after is not null && after.All(c => c == '0'))
            return new WebhookIgnored($"The branch {branch} was deleted.");
        return new WebhookPush(GitSource.IsCommit(after) ? after : null);
    }

    private static (string? Ref, string? After) Payload(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            return (null, null);
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            string? Get(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Get("ref"), Get("after"));
        }
        catch (JsonException)
        {
            // A form-encoded or empty body from a simple caller: no branch named.
            return (null, null);
        }
    }

    private static string Host(Func<string, string?> header) =>
        header("X-GitHub-Event") is not null ? "GitHub"
        : header("X-Gitea-Event") is not null || header("X-Forgejo-Event") is not null ? "Gitea"
        : "GitHub-style";

    private static string Short(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..]
        : reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? $"the tag {reference["refs/tags/".Length..]}"
        : reference;

    private static bool HexEquals(string hex, byte[] expected)
    {
        hex = hex.Trim();
        if (hex.Length != expected.Length * 2)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hex), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
