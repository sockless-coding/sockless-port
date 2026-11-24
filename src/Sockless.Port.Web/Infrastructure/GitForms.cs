using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>What the git source fields of a stack form post (<c>Git.*</c>).</summary>
public sealed class GitInput
{
    public string? Url { get; set; }

    public string? Branch { get; set; } = GitSource.DefaultBranch;

    public string? ComposePath { get; set; } = GitSource.DefaultComposePath;

    /// <summary>The <c>.env</c> in the repository; empty keeps it in Sockless Port.</summary>
    public string? EnvPath { get; set; }

    public GitAuth Auth { get; set; }

    public string? Username { get; set; }

    public string? Token { get; set; }

    public string? PrivateKey { get; set; }

    /// <summary>A key generated on the form, encrypted for the round trip (it expires after an hour).</summary>
    public string? GeneratedKey { get; set; }

    /// <summary>Minutes between checks of the branch; 0 when off.</summary>
    public int PollMinutes { get; set; }

    /// <summary>The public half of <see cref="GeneratedKey"/>, shown to add to the repository. Not posted: read from the encrypted key.</summary>
    [BindNever]
    public string? PublicKey { get; set; }

    /// <summary>Editing: a token or key is stored, so leaving the field empty keeps it.</summary>
    [BindNever]
    public bool HasSecret { get; set; }

    [BindNever]
    public StackRepository? Existing { get; set; }

    public static GitInput From(StackRepository repository) => new()
    {
        Url = repository.Url,
        Branch = repository.Branch,
        ComposePath = repository.ComposePath,
        EnvPath = repository.EnvPath,
        Auth = repository.Auth,
        Username = repository.Username,
        PollMinutes = repository.PollMinutes ?? 0,
        HasSecret = repository.ProtectedSecret is not null,
        Existing = repository,
    };
}

/// <summary>Checks the git source fields and turns them into <see cref="GitSettings"/>.</summary>
public sealed class GitForms(IDataProtectionProvider protection)
{
    private static readonly TimeSpan GeneratedKeyLifetime = TimeSpan.FromHours(1);

    private readonly ITimeLimitedDataProtector _generated = protection.CreateProtector("Sockless.Port.Stacks.GeneratedKey").ToTimeLimitedDataProtector();

    /// <summary>A new deploy key on the form: the public half shown, the private half encrypted in a hidden field until the form is saved.</summary>
    public void Generate(GitInput input, string comment)
    {
        var (privateKey, publicKey) = SshKeys.Generate(comment);
        input.GeneratedKey = _generated.Protect($"{publicKey}\n{privateKey}", GeneratedKeyLifetime);
        input.PublicKey = publicKey;
        input.PrivateKey = null;
        input.Auth = GitAuth.SshKey;
    }

    /// <summary>Restores <see cref="GitInput.PublicKey"/> from the generated key a form posted back; drops it when it expired.</summary>
    public (string PrivateKey, string PublicKey)? Generated(GitInput input)
    {
        if (string.IsNullOrEmpty(input.GeneratedKey))
            return null;
        try
        {
            var payload = _generated.Unprotect(input.GeneratedKey);
            var split = payload.IndexOf('\n', StringComparison.Ordinal);
            input.PublicKey = payload[..split];
            return (payload[(split + 1)..], input.PublicKey);
        }
        catch (CryptographicException)
        {
            input.GeneratedKey = null;
            return null;
        }
    }

    /// <summary>The settings, or null with the problems in <paramref name="state"/> under <c>Git.*</c>.</summary>
    /// <param name="existing">The stack's repository when editing: an empty token or key keeps the stored one.</param>
    public GitSettings? Validate(GitInput input, ModelStateDictionary state, StackRepository? existing)
    {
        var url = input.Url?.Trim() ?? "";
        if (GitSource.UrlProblem(url) is { } urlProblem)
            state.AddModelError("Git.Url", urlProblem);
        var branch = input.Branch?.Trim() ?? "";
        if (GitSource.BranchProblem(branch) is { } branchProblem)
            state.AddModelError("Git.Branch", branchProblem);
        var composePath = GitSource.NormalizePath(input.ComposePath);
        if (GitSource.PathProblem(composePath, "the compose file") is { } composeProblem)
            state.AddModelError("Git.ComposePath", composeProblem);
        var envPath = GitSource.NormalizePath(input.EnvPath);
        if (envPath is not null && GitSource.PathProblem(envPath, "the .env file") is { } envProblem)
            state.AddModelError("Git.EnvPath", envProblem);
        if (input.PollMinutes != 0 && !GitStackTriggers.PollChoices.Contains(input.PollMinutes))
            state.AddModelError("Git.PollMinutes", "Choose how often to check from the list.");
        if (!Enum.IsDefined(input.Auth))
            state.AddModelError("Git.Auth", "Choose how to sign in.");

        var keepsSecret = existing is not null && existing.Auth == input.Auth && existing.ProtectedSecret is not null;
        string? secret = null;
        string? publicKey = null;
        switch (input.Auth)
        {
            case GitAuth.Token:
                secret = string.IsNullOrWhiteSpace(input.Token) ? null : input.Token.Trim();
                if (secret is null && !keepsSecret)
                    state.AddModelError("Git.Token", "Enter the access token.");
                else if (secret is not null && secret.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                    state.AddModelError("Git.Token", "A token has no spaces or line breaks.");
                if (GitSource.UrlProblem(url) is null && GitSource.HttpOrigin(url) is null)
                    state.AddModelError("Git.Auth", "A token is sent over https. For an SSH address, sign in with an SSH key.");
                if (input.Username?.Trim() is { Length: > StackRepository.MaxUsername })
                    state.AddModelError("Git.Username", $"Use at most {StackRepository.MaxUsername} characters.");
                break;

            case GitAuth.SshKey:
                if (Generated(input) is { } generated)
                {
                    secret = generated.PrivateKey;
                    publicKey = generated.PublicKey;
                }
                else if (!string.IsNullOrWhiteSpace(input.PrivateKey))
                {
                    if (SshKeys.Problem(input.PrivateKey) is { } keyProblem)
                        state.AddModelError("Git.PrivateKey", keyProblem);
                    secret = input.PrivateKey;
                }
                else if (!keepsSecret)
                {
                    state.AddModelError("Git.PrivateKey", "Paste a private key, or generate one and add its public key to the repository.");
                }
                if (GitSource.UrlProblem(url) is null && !GitSource.IsSsh(url))
                    state.AddModelError("Git.Auth", "An SSH key needs an SSH address: ssh://… or git@host:owner/repo.git.");
                break;
        }

        if (state.ErrorCount > 0 || composePath is null)
            return null;
        return new GitSettings(url, branch, composePath, envPath, input.Auth, input.Auth == GitAuth.Token ? input.Username?.Trim() : null, secret,
            publicKey ?? (secret is null ? existing?.PublicKey : null), input.PollMinutes == 0 ? null : input.PollMinutes);
    }
}
