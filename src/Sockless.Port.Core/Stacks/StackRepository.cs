namespace Sockless.Port.Stacks;

/// <summary>How the server signs in to a stack's git repository.</summary>
public enum GitAuth
{
    /// <summary>A public repository.</summary>
    None,

    /// <summary>An access token sent as the password over HTTPS.</summary>
    Token,

    /// <summary>A private key for SSH (a deploy key).</summary>
    SshKey,
}

/// <summary>
/// Where a git-backed stack's files come from: a branch of a repository, and the compose file
/// (and optionally the <c>.env</c>) in it. One per stack; removed with it. The checkout is kept in
/// <c>{DataPath}/git/{endpointId}/{name}</c>, and the files are copied to the stack's folder after
/// each fetch, so the review and the diff work as for any other stack.
/// </summary>
public sealed class StackRepository
{
    public const int MaxUrl = 500;
    public const int MaxBranch = 200;
    public const int MaxPath = 300;
    public const int MaxUsername = 200;
    public const int MaxSubject = 200;

    public int Id { get; set; }

    public int StackId { get; set; }

    /// <summary><c>https://host/owner/repo.git</c>, <c>ssh://git@host/owner/repo.git</c> or <c>git@host:owner/repo.git</c>, without credentials.</summary>
    public required string Url { get; set; }

    public required string Branch { get; set; }

    /// <summary>The compose file, relative to the repository's root with forward slashes: <c>compose.yaml</c>, <c>apps/web/compose.yaml</c>.</summary>
    public required string ComposePath { get; set; }

    /// <summary>The <c>.env</c> in the repository; null when it's kept here and edited in Sockless Port.</summary>
    public string? EnvPath { get; set; }

    public GitAuth Auth { get; set; }

    /// <summary>For tokens: the user name sent with it. Most hosts accept any; Bitbucket wants <c>x-token-auth</c>.</summary>
    public string? Username { get; set; }

    /// <summary>The token or the private key, encrypted with Data Protection. Never shown again.</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>The public half of a key Sockless Port generated, to add to the repository as a deploy key.</summary>
    public string? PublicKey { get; set; }

    /// <summary>The SSH server's host keys, as <c>known_hosts</c> lines, learnt at the first connection and required after.</summary>
    public string? KnownHosts { get; set; }

    /// <summary>How often to check the branch for new commits; null when it isn't polled.</summary>
    public int? PollMinutes { get; set; }

    /// <summary>The random part of the webhook's address (<c>/hooks/stacks/{id}</c>); null when webhooks are off.</summary>
    public string? WebhookId { get; set; }

    /// <summary>The webhook's secret, encrypted with Data Protection.</summary>
    public string? ProtectedWebhookSecret { get; set; }

    /// <summary>The commit the stack's files were last fetched from.</summary>
    public string? FetchedCommit { get; set; }

    public string? FetchedSubject { get; set; }

    public DateTimeOffset? FetchedCommitAt { get; set; }

    public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>The commit of the last successful deploy.</summary>
    public string? DeployedCommit { get; set; }

    public string? DeployedSubject { get; set; }

    /// <summary>When the branch was last checked (polled or fetched), successfully or not.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>Why the last check or fetch failed; null when it worked.</summary>
    public string? LastError { get; set; }

    public bool HasWebhook => WebhookId is not null;
}
