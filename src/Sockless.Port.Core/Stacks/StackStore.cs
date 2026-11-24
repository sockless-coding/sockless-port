using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Stacks;

/// <summary>Thrown when a stack can't be saved; the message is for people.</summary>
public sealed class StackRejectedException(string message) : Exception(message);

/// <summary>
/// Where a git-backed stack's compose file is checked out. Compose runs with its folder as the
/// project directory, so relative paths in the file (<c>env_file:</c>, configs) resolve in the checkout.
/// </summary>
public sealed record StackSource(string Directory, string ComposeFile);

/// <summary>A git-backed stack's settings as a form gives them.</summary>
/// <param name="Secret">A new token or private key; null keeps the stored one (when the sign-in kind is unchanged).</param>
/// <param name="PublicKey">The public half when <paramref name="Secret"/> is a key Sockless Port generated.</param>
public sealed record GitSettings(string Url, string Branch, string ComposePath, string? EnvPath, GitAuth Auth, string? Username, string? Secret,
    string? PublicKey, int? PollMinutes);

/// <summary>
/// Stacks Sockless Port manages: a row per stack in the database, and its files on disk. A
/// git-backed stack also has a <see cref="StackRepository"/> row and a checkout.
/// Singleton; each call uses its own database scope.
/// </summary>
public sealed class StackStore(IServiceScopeFactory scopes, IOptions<PortOptions> options, TimeProvider time, IDataProtectionProvider protection)
{
    private const string Purpose = "Sockless.Port.Stacks.Git";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public StackFiles Files { get; } = new(options.Value.StacksPath);

    /// <summary>The checkout of a git-backed stack: <c>{DataPath}/git/{endpointId}/{name}</c>.</summary>
    public string CheckoutDirectory(int endpointId, string name)
    {
        if (StackNames.Problem(name) is not null)
            throw new ArgumentException($"'{name}' isn't a valid stack name.", nameof(name));
        return Path.Combine(options.Value.GitPath, endpointId.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
    }

    /// <summary>Where the stack's compose file is in its checkout.</summary>
    public StackSource Source(int endpointId, string name, StackRepository repository)
    {
        var file = Path.GetFullPath(Path.Combine(CheckoutDirectory(endpointId, name), repository.ComposePath));
        return new StackSource(Path.GetDirectoryName(file)!, file);
    }

    public async Task<IReadOnlyList<StackDefinition>> ListAsync(int endpointId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.Stacks.AsNoTracking().Where(s => s.EndpointId == endpointId).OrderBy(s => s.Name).ToListAsync(cancellationToken);
    }

    public async Task<StackDefinition?> GetAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
    }

    public async Task<StackDefinition?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    /// <exception cref="StackRejectedException">The name is invalid or taken.</exception>
    /// <param name="kind">How it deploys: Compose, or <c>docker stack deploy</c> on a swarm.</param>
    /// <param name="template">The template it's made from, as <see cref="StackDefinition.Template"/>.</param>
    public async Task<StackDefinition> CreateAsync(int endpointId, string name, StackContent content, bool adopted, StackKind kind, CancellationToken cancellationToken = default,
        string? template = null)
    {
        if (StackNames.Problem(name) is { } problem)
            throw new StackRejectedException(problem);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Stacks.AnyAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken))
            throw new StackRejectedException($"There's already a stack named {name} on this endpoint.");

        var now = time.GetUtcNow();
        var stack = new StackDefinition { EndpointId = endpointId, Name = name, Kind = kind, Adopted = adopted, Template = template, CreatedAt = now, UpdatedAt = now };
        // Files first: a row without files would be a stack that can't be opened.
        await Files.WriteAsync(endpointId, name, content, cancellationToken);
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(cancellationToken);
        return stack;
    }

    /// <returns>False if the stack doesn't exist.</returns>
    public async Task<bool> SaveAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return false;
        await Files.WriteAsync(endpointId, name, content, cancellationToken);
        stack.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Records a successful deploy of <paramref name="content"/>, and for a git-backed stack the commit it came from.</summary>
    public async Task MarkDeployedAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default,
        string? commit = null, string? subject = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return;
        await Files.WriteDeployedAsync(endpointId, name, content, cancellationToken);
        stack.DeployedAt = time.GetUtcNow();
        if (commit is not null && await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stack.Id, cancellationToken) is { } repository)
        {
            repository.DeployedCommit = commit;
            repository.DeployedSubject = subject is null ? null : Truncate(subject, StackRepository.MaxSubject);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>After a <c>down</c>: nothing is deployed any more.</summary>
    public async Task MarkDownAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return;
        Files.DeleteDeployed(endpointId, name);
        stack.DeployedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Forgets the stack and deletes its files. Its containers are left alone.</summary>
    /// <returns>False if the stack doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return false;
        db.Stacks.Remove(stack);
        await db.SaveChangesAsync(cancellationToken);
        Files.Delete(endpointId, name);
        DeleteCheckout(endpointId, name);
        return true;
    }

    // ---------------------------------------------------------------- git-backed stacks

    public async Task<StackRepository?> GetRepositoryAsync(int stackId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.StackRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.StackId == stackId, cancellationToken);
    }

    /// <summary>The endpoint's git-backed stacks' repositories, by stack ID.</summary>
    public async Task<IReadOnlyDictionary<int, StackRepository>> RepositoriesAsync(int endpointId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stackIds = db.Stacks.Where(s => s.EndpointId == endpointId).Select(s => s.Id);
        return await db.StackRepositories.AsNoTracking().Where(r => stackIds.Contains(r.StackId)).ToDictionaryAsync(r => r.StackId, cancellationToken);
    }

    /// <summary>Every git-backed stack, for polling.</summary>
    public async Task<IReadOnlyList<(StackDefinition Stack, StackRepository Repository)>> AllRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rows = await (from r in db.StackRepositories.AsNoTracking()
                          join s in db.Stacks.AsNoTracking() on r.StackId equals s.Id
                          select new { Stack = s, Repository = r }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.Stack, r.Repository))];
    }

    /// <summary>The stack whose webhook address ends in <paramref name="webhookId"/>.</summary>
    public async Task<(StackDefinition Stack, StackRepository Repository)?> FindWebhookAsync(string webhookId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var row = await (from r in db.StackRepositories.AsNoTracking()
                         join s in db.Stacks.AsNoTracking() on r.StackId equals s.Id
                         where r.WebhookId == webhookId
                         select new { Stack = s, Repository = r }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Stack, row.Repository);
    }

    /// <summary>The repository and how to sign in to it, with the secret decrypted for one run.</summary>
    /// <exception cref="GitException">The secret can't be decrypted: the Data Protection keys are gone.</exception>
    public GitRemote Remote(StackRepository repository) =>
        new(repository.Url, repository.Auth, repository.Username, Unprotect(repository.ProtectedSecret, "token or key"), repository.KnownHosts);

    /// <summary>What a fetch of the stack needs.</summary>
    /// <exception cref="GitException">The secret can't be decrypted.</exception>
    public GitCheckoutRequest CheckoutRequest(StackRepository repository) =>
        new(Remote(repository), repository.Branch, repository.ComposePath, repository.EnvPath);

    /// <exception cref="GitException">The secret can't be decrypted.</exception>
    public string? WebhookSecret(StackRepository repository) => Unprotect(repository.ProtectedWebhookSecret, "webhook secret");

    /// <summary>
    /// Connects the stack to a repository, or changes its settings. A new address drops the
    /// checkout (it's another repository), and a new host the SSH host keys learnt from the old one.
    /// The files aren't fetched here: start a fetch after.
    /// </summary>
    /// <returns>Null if the stack doesn't exist.</returns>
    /// <exception cref="StackRejectedException">The sign-in changed kind without a new secret.</exception>
    public async Task<StackRepository?> SaveRepositoryAsync(int endpointId, string name, GitSettings settings, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return null;
        var repository = await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stack.Id, cancellationToken);
        var dropCheckout = false;
        if (repository is null)
        {
            repository = new StackRepository { StackId = stack.Id, Url = settings.Url, Branch = settings.Branch, ComposePath = settings.ComposePath, Auth = settings.Auth };
            db.StackRepositories.Add(repository);
            // Whatever was checked out under this name before belongs to no repository now.
            dropCheckout = true;
        }
        else if (repository.Url != settings.Url)
        {
            dropCheckout = true;
            if (GitSource.Host(repository.Url) != GitSource.Host(settings.Url))
                repository.KnownHosts = null;
            repository.FetchedCommit = null;
            repository.FetchedSubject = null;
            repository.FetchedCommitAt = null;
        }
        Apply(repository, settings, isNew: repository.Id == 0);
        stack.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        if (dropCheckout)
            DeleteCheckout(endpointId, name);
        return repository;
    }

    /// <summary>
    /// A new stack whose files were just fetched into <paramref name="stagedCheckout"/>, which
    /// becomes its checkout.
    /// </summary>
    /// <param name="env">The stack's own <c>.env</c>, when it isn't taken from the repository.</param>
    /// <exception cref="StackRejectedException">The name is invalid or taken.</exception>
    public async Task<StackDefinition> CreateFromGitAsync(int endpointId, string name, StackKind kind, GitSettings settings, GitFetchResult fetched,
        string stagedCheckout, string? env, CancellationToken cancellationToken = default)
    {
        var content = StackContent.Normalize(fetched.Compose, fetched.Env ?? env);
        var stack = await CreateAsync(endpointId, name, content, adopted: false, kind, cancellationToken);
        try
        {
            var target = CheckoutDirectory(endpointId, name);
            DeleteCheckout(endpointId, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(stagedCheckout, target);

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            var repository = new StackRepository { StackId = stack.Id, Url = settings.Url, Branch = settings.Branch, ComposePath = settings.ComposePath, Auth = settings.Auth };
            Apply(repository, settings, isNew: true);
            Fetched(repository, fetched);
            db.StackRepositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
            return stack;
        }
        catch
        {
            await DeleteAsync(endpointId, name, CancellationToken.None);
            throw;
        }
    }

    /// <summary>A fetch succeeded: the stack's files become the repository's (the <c>.env</c> only when it comes from there).</summary>
    public async Task RecordFetchAsync(int endpointId, string name, GitFetchResult fetched, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        var repository = stack is null ? null : await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stack.Id, cancellationToken);
        if (stack is null || repository is null)
            return;
        var saved = await Files.ReadAsync(endpointId, name, cancellationToken) ?? StackContent.Empty;
        var content = StackContent.Normalize(fetched.Compose, fetched.Env ?? saved.Env);
        if (content != saved)
        {
            await Files.WriteAsync(endpointId, name, content, cancellationToken);
            stack.UpdatedAt = time.GetUtcNow();
        }
        Fetched(repository, fetched);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A check of the branch (a poll, or a fetch that failed): when, why it failed, and host keys learnt.</summary>
    /// <returns>The error the repository had before, to tell a new failure from one that goes on.</returns>
    public async Task<string?> RecordCheckAsync(int stackId, string? error, string? knownHosts = null, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var repository = await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stackId, cancellationToken);
        if (repository is null)
            return null;
        var previous = repository.LastError;
        repository.CheckedAt = time.GetUtcNow();
        repository.LastError = error is null ? null : Truncate(error, 2000);
        if (knownHosts is not null && repository.KnownHosts is null)
            repository.KnownHosts = knownHosts;
        await db.SaveChangesAsync(cancellationToken);
        return previous;
    }

    /// <summary>Turns the stack's webhook on with a new address and secret (replacing any old ones), or off.</summary>
    /// <returns>The new secret, or null when turned off or the stack has no repository.</returns>
    public async Task<string?> SetWebhookAsync(int stackId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var repository = await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stackId, cancellationToken);
        if (repository is null)
            return null;
        string? secret = null;
        if (enabled)
        {
            secret = GitWebhooks.NewSecret();
            repository.WebhookId = GitWebhooks.NewId();
            repository.ProtectedWebhookSecret = _protector.Protect(secret);
        }
        else
        {
            repository.WebhookId = null;
            repository.ProtectedWebhookSecret = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        return secret;
    }

    /// <summary>Forgets the SSH host keys, so the next connection learns them again.</summary>
    public async Task ForgetHostKeysAsync(int stackId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stackId, cancellationToken) is { } repository)
        {
            repository.KnownHosts = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The stack stops following its repository: its files stay, and become editable here.</summary>
    public async Task<bool> DisconnectRepositoryAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        var repository = stack is null ? null : await db.StackRepositories.FirstOrDefaultAsync(r => r.StackId == stack.Id, cancellationToken);
        if (repository is null)
            return false;
        db.StackRepositories.Remove(repository);
        await db.SaveChangesAsync(cancellationToken);
        DeleteCheckout(endpointId, name);
        return true;
    }

    /// <summary>Deletes the checkout; it's fetched again when needed.</summary>
    public void DeleteCheckout(int endpointId, string name) => DeleteTree(CheckoutDirectory(endpointId, name));

    /// <summary>Deletes a folder git wrote. Git makes its object files read-only, which Windows won't delete otherwise.</summary>
    public static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(directory, recursive: true);
    }

    private void Apply(StackRepository repository, GitSettings settings, bool isNew)
    {
        var authChanged = isNew || repository.Auth != settings.Auth;
        repository.Url = settings.Url;
        repository.Branch = settings.Branch;
        repository.ComposePath = settings.ComposePath;
        repository.EnvPath = settings.EnvPath;
        repository.Auth = settings.Auth;
        repository.Username = settings.Auth == GitAuth.Token && !string.IsNullOrWhiteSpace(settings.Username) ? settings.Username.Trim() : null;
        repository.PollMinutes = settings.PollMinutes;
        if (settings.Auth == GitAuth.None)
        {
            repository.ProtectedSecret = null;
            repository.PublicKey = null;
        }
        else if (settings.Secret is { Length: > 0 } secret)
        {
            repository.ProtectedSecret = _protector.Protect(settings.Auth == GitAuth.SshKey ? SshKeys.Normalize(secret) : secret.Trim());
            repository.PublicKey = settings.Auth == GitAuth.SshKey ? settings.PublicKey : null;
        }
        else if (authChanged)
        {
            throw new StackRejectedException(settings.Auth == GitAuth.Token ? "Enter the token." : "Paste or generate an SSH key.");
        }
        // The branch may have moved on under the new settings: check it soon.
        repository.CheckedAt = null;
    }

    private void Fetched(StackRepository repository, GitFetchResult fetched)
    {
        repository.FetchedCommit = fetched.Commit.Sha;
        repository.FetchedSubject = Truncate(fetched.Commit.Subject, StackRepository.MaxSubject);
        repository.FetchedCommitAt = fetched.Commit.At;
        repository.FetchedAt = time.GetUtcNow();
        repository.CheckedAt = repository.FetchedAt;
        repository.LastError = null;
        if (fetched.KnownHosts is not null && repository.KnownHosts is null)
            repository.KnownHosts = fetched.KnownHosts;
    }

    private string? Unprotect(string? protectedValue, string what)
    {
        if (protectedValue is null)
            return null;
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new GitException($"The stored {what} can't be decrypted: the server's Data Protection keys changed. Enter it again in the stack's repository settings.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
