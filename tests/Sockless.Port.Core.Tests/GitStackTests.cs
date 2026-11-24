using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Core.Tests;

public sealed class GitSourceTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://gitea.example.com:3000/team/app")]
    [InlineData("http://git.lan/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("ssh://git@git.lan:2222/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("deploy@git.lan:/srv/git/app.git")]
    public void Addresses_of_the_allowed_transports_are_accepted(string url) => Assert.Null(GitSource.UrlProblem(url));

    [Theory]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("git://github.com/owner/repo.git")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("https://github.com")]
    [InlineData("C:/repos/app")]
    [InlineData("/srv/git/app.git")]
    [InlineData("https://github.com/owner/repo .git")]
    public void Other_addresses_are_refused(string url) => Assert.NotNull(GitSource.UrlProblem(url));

    [Fact]
    public void Credentials_in_the_address_are_refused_with_a_pointer_to_the_token_field()
    {
        Assert.Contains("Sign in", GitSource.UrlProblem("https://user:ghp_secret@github.com/owner/repo.git"));
        Assert.Contains("Sign in", GitSource.UrlProblem("https://ghp_secret@github.com/owner/repo.git"));
        Assert.NotNull(GitSource.UrlProblem("ssh://git:password@github.com/owner/repo.git"));
    }

    [Theory]
    [InlineData("git@github.com:owner/repo.git", true)]
    [InlineData("ssh://git@host/repo", true)]
    [InlineData("https://github.com/owner/repo", false)]
    public void Ssh_addresses_are_told_apart(string url, bool ssh) => Assert.Equal(ssh, GitSource.IsSsh(url));

    [Fact]
    public void Hosts_and_origins_are_read_from_either_form()
    {
        Assert.Equal("github.com", GitSource.Host("git@GitHub.com:owner/repo.git"));
        Assert.Equal("git.lan", GitSource.Host("https://git.lan:3000/a/b"));
        Assert.Equal("https://git.lan:3000/", GitSource.HttpOrigin("https://git.lan:3000/a/b.git"));
        Assert.Null(GitSource.HttpOrigin("git@github.com:owner/repo.git"));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/2026.11")]
    [InlineData("feature_x-1")]
    public void Branch_names_git_accepts_are_accepted(string branch) => Assert.Null(GitSource.BranchProblem(branch));

    [Theory]
    [InlineData("")]
    [InlineData("refs/heads/main")]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("main.lock")]
    [InlineData("feature/")]
    [InlineData("a/.hidden")]
    [InlineData("main:other")]
    public void Other_branch_names_are_refused(string branch) => Assert.NotNull(GitSource.BranchProblem(branch));

    [Theory]
    [InlineData("./deploy\\compose.yml", "deploy/compose.yml")]
    [InlineData(" compose.yaml ", "compose.yaml")]
    [InlineData("", null)]
    public void Paths_are_normalised(string path, string? expected) => Assert.Equal(expected, GitSource.NormalizePath(path));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../secrets.env")]
    [InlineData("apps/../../x")]
    [InlineData(".git/config")]
    [InlineData("a//b")]
    [InlineData("C:/x")]
    public void Paths_outside_the_repository_are_refused(string path) => Assert.NotNull(GitSource.PathProblem(path, "the compose file"));

    [Fact]
    public void Commit_links_point_at_the_host_when_it_has_pages()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        Assert.Equal($"https://github.com/owner/repo/commit/{sha}", GitSource.CommitLink("https://github.com/owner/repo.git", sha));
        Assert.Equal($"https://github.com/owner/repo/commit/{sha}", GitSource.CommitLink("git@github.com:owner/repo.git", sha));
        Assert.Null(GitSource.CommitLink("git@git.lan:owner/repo.git", sha));
        Assert.Null(GitSource.CommitLink("https://github.com/owner/repo.git", "not-a-commit"));
    }
}

public sealed class GitWebhookTests
{
    private const string Secret = "s3cret-for-the-hook";
    private static readonly byte[] PushToMain = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","after":"0123456789abcdef0123456789abcdef01234567"}""");

    private static Func<string, string?> Headers(params (string Name, string Value)[] headers) =>
        name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Sign(byte[] body, string secret = Secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void GitHub_signatures_are_checked_against_the_body()
    {
        Assert.Equal("GitHub", GitWebhooks.Verify(Headers(("X-GitHub-Event", "push"), ("X-Hub-Signature-256", $"sha256={Sign(PushToMain)}")), PushToMain, Secret));
        Assert.Null(GitWebhooks.Verify(Headers(("X-Hub-Signature-256", $"sha256={Sign(PushToMain, "other")}")), PushToMain, Secret));
        Assert.Null(GitWebhooks.Verify(Headers(("X-Hub-Signature-256", $"sha256={Sign(PushToMain)}")), [.. PushToMain, (byte)' '], Secret));
        Assert.Null(GitWebhooks.Verify(Headers(("X-Hub-Signature-256", "sha256=zz")), PushToMain, Secret));
    }

    [Fact]
    public void Gitea_signatures_and_GitLab_tokens_are_accepted()
    {
        Assert.Equal("Gitea", GitWebhooks.Verify(Headers(("X-Gitea-Signature", Sign(PushToMain))), PushToMain, Secret));
        Assert.Equal("GitLab", GitWebhooks.Verify(Headers(("X-Gitlab-Token", Secret)), PushToMain, Secret));
        Assert.Null(GitWebhooks.Verify(Headers(("X-Gitlab-Token", "guess")), PushToMain, Secret));
    }

    [Fact]
    public void A_call_without_the_secret_is_refused()
    {
        Assert.Null(GitWebhooks.Verify(Headers(), PushToMain, Secret));
        // A wrong signature isn't saved by a right token next to it.
        Assert.Null(GitWebhooks.Verify(Headers(("X-Hub-Signature-256", "sha256=00"), ("X-Gitlab-Token", Secret)), PushToMain, Secret));
    }

    [Fact]
    public void Pushes_to_the_branch_redeploy_and_everything_else_is_ignored()
    {
        Assert.IsType<WebhookPing>(GitWebhooks.Read(Headers(("X-GitHub-Event", "ping")), "{}"u8, "main"));
        var push = Assert.IsType<WebhookPush>(GitWebhooks.Read(Headers(("X-GitHub-Event", "push")), PushToMain, "main"));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", push.Commit);
        Assert.IsType<WebhookPush>(GitWebhooks.Read(Headers(("X-Gitlab-Event", "Push Hook")), PushToMain, "main"));

        Assert.Contains("develop", Assert.IsType<WebhookIgnored>(GitWebhooks.Read(Headers(("X-GitHub-Event", "push")), PushToMain, "develop")).Reason);
        Assert.IsType<WebhookIgnored>(GitWebhooks.Read(Headers(("X-GitHub-Event", "issues")), PushToMain, "main"));
        Assert.IsType<WebhookIgnored>(GitWebhooks.Read(Headers(("X-Gitlab-Event", "Tag Push Hook")), PushToMain, "main"));
        Assert.Contains("tag", Assert.IsType<WebhookIgnored>(GitWebhooks.Read(Headers(("X-Gitea-Event", "push")), """{"ref":"refs/tags/v1"}"""u8, "main")).Reason);
        Assert.Contains("deleted", Assert.IsType<WebhookIgnored>(GitWebhooks.Read(Headers(("X-GitHub-Event", "push")),
            """{"ref":"refs/heads/main","after":"0000000000000000000000000000000000000000"}"""u8, "main")).Reason);
    }

    [Fact]
    public void Callers_that_name_no_event_redeploy()
    {
        Assert.Equal(new WebhookPush(null), GitWebhooks.Read(Headers(), [], "main"));
        Assert.Equal(new WebhookPush(null), GitWebhooks.Read(Headers(), "not json"u8, "main"));
    }

    [Fact]
    public void Ids_and_secrets_are_random_hex()
    {
        Assert.Matches("^[0-9a-f]{32}$", GitWebhooks.NewId());
        Assert.Matches("^[0-9a-f]{64}$", GitWebhooks.NewSecret());
        Assert.NotEqual(GitWebhooks.NewSecret(), GitWebhooks.NewSecret());
    }
}

public sealed class SshKeyTests
{
    [Fact]
    public void A_generated_key_is_an_unencrypted_OpenSSH_key_with_its_public_line()
    {
        var (privateKey, publicKey) = SshKeys.Generate("sockless-port web@local");
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", privateKey);
        Assert.EndsWith("-----END OPENSSH PRIVATE KEY-----\n", privateKey);
        Assert.Null(SshKeys.Problem(privateKey));
        Assert.Matches(@"^ecdsa-sha2-nistp256 AAAA[A-Za-z0-9+/=]+ sockless-port web@local$", publicKey);
        Assert.NotEqual(SshKeys.Generate("x").PrivateKey, SshKeys.Generate("x").PrivateKey);
    }

    [Fact]
    public void Ssh_keygen_reads_the_generated_key_and_derives_the_same_public_key()
    {
        var keygen = Run("ssh-keygen", ["-?"]);
        Assert.SkipWhen(keygen is null, "ssh-keygen isn't installed here.");
        var (privateKey, publicKey) = SshKeys.Generate("test");
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(folder, "id");
            File.WriteAllText(file, privateKey);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var derived = Run("ssh-keygen", ["-y", "-f", file]);
            Assert.NotNull(derived);
            Assert.Equal(string.Join(' ', publicKey.Split(' ')[..2]), string.Join(' ', derived.Trim().Split(' ')[..2]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Pasted_keys_that_wont_work_are_explained()
    {
        Assert.NotNull(SshKeys.Problem(""));
        Assert.Contains("public key", SshKeys.Problem("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl me@host"));
        Assert.Contains("isn't a private key", SshKeys.Problem("hello"));
        Assert.Contains("passphrase", SshKeys.Problem("-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nabc\n-----END RSA PRIVATE KEY-----"));

        // OpenSSH's own format names its cipher: anything but "none" has a passphrase.
        var encrypted = Encoding.ASCII.GetBytes("openssh-key-v1\0").Concat(new byte[] { 0, 0, 0, 10 }).Concat(Encoding.ASCII.GetBytes("aes256-ctr")).ToArray();
        Assert.Contains("passphrase", SshKeys.Problem($"-----BEGIN OPENSSH PRIVATE KEY-----\n{Convert.ToBase64String(encrypted)}\n-----END OPENSSH PRIVATE KEY-----"));
    }

    [Fact]
    public void Host_keys_get_the_fingerprint_ssh_keygen_prints()
    {
        // The SHA-256 of the decoded blob, base64 without padding.
        var blob = Convert.ToBase64String(Encoding.ASCII.GetBytes("not really a key"));
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes("not really a key"))).TrimEnd('=');
        var key = Assert.Single(SshKeys.HostKeys($"# comment\ngithub.com ssh-ed25519 {blob}\nbroken line\n"));
        Assert.Equal(new SshHostKey("github.com", "ssh-ed25519", expected), key);
    }

    [Fact]
    public void Pasted_keys_are_normalised_for_ssh()
    {
        Assert.Equal("-----BEGIN X-----\nabc\n-----END X-----\n", SshKeys.Normalize("  -----BEGIN X-----\r\nabc  \r\n\r\n-----END X-----"));
    }

    private static string? Run(string file, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed class GitCliTests
{
    [Fact]
    public void A_token_reaches_git_through_its_environment_scoped_to_the_host()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var env = GitCli.Environment(new GitRemote("https://git.lan:3000/team/app.git", GitAuth.Token, "bot", "t0ken"), folder, "https:http:ssh");
            Assert.Equal("1", env["GIT_CONFIG_NOSYSTEM"]);
            Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("https:http:ssh", env["GIT_ALLOW_PROTOCOL"]);
            Assert.Equal(Path.Combine(folder, "gitconfig"), env["GIT_CONFIG_GLOBAL"]);
            var keys = Enumerable.Range(0, int.Parse(env["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture))
                .ToDictionary(i => env[$"GIT_CONFIG_KEY_{i}"], i => env[$"GIT_CONFIG_VALUE_{i}"]);
            Assert.Equal($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("bot:t0ken"))}", keys["http.https://git.lan:3000/.extraHeader"]);
            Assert.Equal("never", keys["protocol.file.allow"]);
            Assert.False(env.ContainsKey("GIT_SSH_COMMAND"));
            Assert.False(env.ContainsKey("DOCKER_CONFIG"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_token_for_an_ssh_address_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Throws<GitException>(() => GitCli.Environment(new GitRemote("git@github.com:o/r.git", GitAuth.Token, null, "t"), folder, "https:http:ssh"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_ssh_key_is_a_private_file_and_host_keys_are_learnt_once_then_required()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var (key, _) = SshKeys.Generate("test");
            var first = GitCli.Environment(new GitRemote("git@github.com:o/r.git", GitAuth.SshKey, null, key), folder, "https:http:ssh");
            var command = first["GIT_SSH_COMMAND"];
            Assert.Contains("StrictHostKeyChecking=accept-new", command);
            Assert.Contains("BatchMode=yes", command);
            Assert.Contains($"-i '{Path.Combine(folder, "id_key").Replace('\\', '/')}'", command);
            Assert.Equal(key, File.ReadAllText(Path.Combine(folder, "id_key")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "id_key")));

            var again = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var known = "github.com ssh-ed25519 AAAA\n";
                var later = GitCli.Environment(new GitRemote("git@github.com:o/r.git", GitAuth.SshKey, null, key, known), again, "https:http:ssh");
                Assert.Contains("StrictHostKeyChecking=yes", later["GIT_SSH_COMMAND"]);
                Assert.Equal(known, File.ReadAllText(Path.Combine(again, "known_hosts")));
            }
            finally
            {
                Directory.Delete(again, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Compose_runs_a_git_stack_from_its_checkout_with_the_stack_env()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, ".env"), "A=1\n");
            var source = new StackSource(Path.Combine(folder, "checkout", "apps"), Path.Combine(folder, "checkout", "apps", "compose.yml"));
            var arguments = ComposeCli.Arguments(new ComposeTool("docker", ["compose"], "5.7.0"), new ComposeRequest("web", ["up"], ProjectDirectory: folder, Source: source));
            Assert.Equal(
                ["compose", "--ansi", "never", "--progress", "plain", "--project-name", "web", "--project-directory", source.Directory,
                 "--file", source.ComposeFile, "--env-file", Path.Combine(folder, ".env"), "up"],
                arguments);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>A git repository on disk to fetch from, made with the git on this machine.</summary>
internal sealed class TestRepository : IDisposable
{
    private TestRepository(string root) => Root = root;

    public string Root { get; }

    public string Work => Path.Combine(Root, "work");

    /// <summary>A file:// address for the work repository.</summary>
    public string Url => new Uri(Work).AbsoluteUri;

    public static TestRepository? Create()
    {
        var repository = new TestRepository(Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"repo-{Guid.NewGuid():n}"));
        Directory.CreateDirectory(repository.Work);
        if (repository.TryGit("-c", "init.defaultBranch=main", "init", "--quiet", repository.Work) is null)
        {
            repository.Dispose();
            return null;
        }
        return repository;
    }

    /// <summary>Writes the files and commits them; returns the commit.</summary>
    public string Commit(string message, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(Work, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Git("-C", Work, "add", "--all");
        Git("-C", Work, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "-m", message);
        return Git("-C", Work, "rev-parse", "HEAD").Trim();
    }

    /// <summary>Commits a symbolic link (whatever the platform can make) pointing at <paramref name="target"/>.</summary>
    public string CommitLink(string path, string target)
    {
        var blob = GitWithInput(target, "-C", Work, "hash-object", "-w", "--stdin").Trim();
        Git("-C", Work, "update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
        Git("-C", Work, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", $"link {path}");
        return Git("-C", Work, "rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] arguments) => TryGit(arguments) ?? throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed");

    private string? TryGit(params string[] arguments) => Run(null, arguments);

    private string GitWithInput(string input, params string[] arguments) => Run(input, arguments) ?? throw new InvalidOperationException("git failed");

    private static string? Run(string? input, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false,
            };
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            StackStore.DeleteTree(Root);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Fetching from real (local) repositories with the git on this machine, and git-backed stacks in the store and jobs.</summary>
public sealed class GitStackIntegrationTests : IAsyncLifetime
{
    private const string Compose = "services:\n  web:\n    image: nginx:${TAG}\n    env_file: app.env\n";
    private StackHarness _harness = null!;
    private TestRepository _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await StackHarness.CreateAsync();
        var (tool, problem) = await _harness.Git.StatusAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(tool is null, $"git isn't available: {problem}");
        _repository = TestRepository.Create() ?? throw new InvalidOperationException("Couldn't make a repository.");
    }

    public async ValueTask DisposeAsync()
    {
        _repository?.Dispose();
        await _harness.DisposeAsync();
    }

    private GitCheckoutRequest Request(string branch = "main", string composePath = "apps/web/compose.yaml", string? envPath = null) =>
        new(new GitRemote(_repository.Url), branch, composePath, envPath);

    private string Checkout => Path.Combine(_harness.DataPath, "checkout");

    [Fact]
    public async Task A_branch_is_fetched_and_its_files_read_then_fetched_again_after_a_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = _repository.Commit("First", ("apps/web/compose.yaml", Compose), ("apps/web/app.env", "A=1\n"), ("apps/web/.env", "TAG=1.27\n"));

        var lines = new List<string>();
        var fetched = await _harness.StackGit.FetchAsync(Request(envPath: "apps/web/.env"), Checkout, null, lines.Add, ct);
        Assert.Equal(first, fetched.Commit.Sha);
        Assert.Equal("First", fetched.Commit.Subject);
        Assert.Equal("Test", fetched.Commit.Author);
        Assert.Equal(Compose, fetched.Compose);
        Assert.Equal("TAG=1.27\n", fetched.Env);
        Assert.Null(fetched.KnownHosts);
        Assert.True(File.Exists(Path.Combine(Checkout, "apps", "web", "app.env")));
        Assert.Contains(lines, l => l.StartsWith("Checked out", StringComparison.Ordinal));

        var second = _repository.Commit("Second", ("apps/web/compose.yaml", Compose.Replace("nginx", "caddy", StringComparison.Ordinal)));
        var (head, _) = await _harness.StackGit.HeadAsync(new GitRemote(_repository.Url), "main", ct);
        Assert.Equal(second, head);

        var again = await _harness.StackGit.FetchAsync(Request(), Checkout, null, null, ct);
        Assert.Equal(second, again.Commit.Sha);
        Assert.Contains("caddy", again.Compose);
        Assert.Null(again.Env);

        var commits = await _harness.StackGit.CommitsAsync(Checkout, first, second, 10, ct);
        Assert.Equal(new[] { "Second" }, commits!.Select(c => c.Subject));
        Assert.Null(await _harness.StackGit.CommitsAsync(Checkout, "0123456789abcdef0123456789abcdef01234567", second, 10, ct));

        // A deploy that lost its checkout gets the reviewed commit back, not the branch's newest.
        StackStore.DeleteTree(Checkout);
        var pinned = await _harness.StackGit.FetchAsync(Request(), Checkout, first, null, ct);
        Assert.Equal(first, pinned.Commit.Sha);
        Assert.DoesNotContain("caddy", pinned.Compose);
    }

    [Fact]
    public async Task Missing_branches_and_files_are_explained()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.Commit("Only a readme", ("README.md", "hi\n"));

        var branch = await Assert.ThrowsAsync<GitException>(() => _harness.StackGit.FetchAsync(Request(branch: "develop"), Checkout, null, null, ct));
        Assert.Contains("Check the branch", branch.Message);
        await Assert.ThrowsAsync<GitException>(() => _harness.StackGit.HeadAsync(new GitRemote(_repository.Url), "develop", ct));

        var file = await Assert.ThrowsAsync<GitException>(() => _harness.StackGit.FetchAsync(Request(), Checkout, null, null, ct));
        Assert.Equal("The compose file isn't in the repository at apps/web/compose.yaml on main.", file.Message);
    }

    [Fact]
    public async Task A_symbolic_link_is_checked_out_as_a_file_so_it_cant_reach_outside()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.Commit("Files", ("README.md", "hi\n"));
        var outside = Path.Combine(_harness.DataPath, "port.db");
        File.WriteAllText(outside, "secret database");
        _repository.CommitLink("compose.yaml", outside);

        var fetched = await _harness.StackGit.FetchAsync(Request(composePath: "compose.yaml"), Checkout, null, null, ct);
        Assert.Equal(outside + "\n", fetched.Compose);
        Assert.DoesNotContain("secret", fetched.Compose);
    }

    [Fact]
    public async Task Other_transports_are_refused_by_git_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.Commit("First", ("compose.yaml", Compose));
        var server = new GitCli(Microsoft.Extensions.Options.Options.Create(_harness.Options), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitCli>.Instance);
        var git = new StackGit(server, Microsoft.Extensions.Logging.Abstractions.NullLogger<StackGit>.Instance);
        var refused = await Assert.ThrowsAsync<GitException>(() => git.FetchAsync(Request(composePath: "compose.yaml"), Checkout, null, null, ct));
        Assert.Contains("Only https, http and ssh are allowed.", refused.Message);
    }

    [Fact]
    public async Task A_git_stack_is_created_fetched_by_a_job_deployed_on_record_and_disconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = _repository.Commit("First", ("apps/web/compose.yaml", Compose), ("apps/web/app.env", "A=1\n"));
        var settings = new GitSettings(_repository.Url, "main", "apps/web/compose.yaml", null, GitAuth.None, null, null, null, 15);
        var staged = Path.Combine(_harness.Options.TempPath, "staged");
        var fetched = await _harness.StackGit.FetchAsync(Request(), staged, null, null, ct);

        var stack = await _harness.Store.CreateFromGitAsync(1, "web", StackKind.Compose, settings, fetched, staged, "TAG=1.27", ct);
        Assert.False(Directory.Exists(staged));
        var checkout = _harness.Store.CheckoutDirectory(1, "web");
        Assert.True(File.Exists(Path.Combine(checkout, "apps", "web", "compose.yaml")));
        Assert.Equal(StackContent.Normalize(Compose, "TAG=1.27"), await _harness.Store.Files.ReadAsync(1, "web", ct));
        var repository = (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!;
        Assert.Equal(first, repository.FetchedCommit);
        Assert.Equal(15, repository.PollMinutes);
        Assert.Equal(new StackSource(Path.Combine(checkout, "apps", "web"), Path.Combine(checkout, "apps", "web", "compose.yaml")),
            _harness.Store.Source(1, "web", repository));

        // A deploy records the commit it came from.
        await _harness.Store.MarkDeployedAsync(1, "web", StackContent.Normalize(Compose, "TAG=1.27"), ct, first, "First");
        Assert.Equal(first, (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!.DeployedCommit);

        // A fetch job needs no engine; the local .env stays.
        var second = _repository.Commit("Second", ("apps/web/compose.yaml", Compose + "  db:\n    image: postgres:17\n"));
        var offline = new EngineConnection(1, "local", DockerAddress.Parse("unix:///nonexistent/test.sock"), EndpointKind.Local);
        var job = await _harness.Jobs.StartAsync(offline, "web", StackOperation.Fetch);
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), ct))
        {
        }
        var done = Assert.IsType<StackJobSucceeded>(job.Status);
        Assert.Contains("Review the changes", done.Message);
        Assert.Equal(second, (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!.FetchedCommit);
        var saved = (await _harness.Store.Files.ReadAsync(1, "web", ct))!;
        Assert.Contains("postgres:17", saved.Compose);
        Assert.Equal("TAG=1.27\n", saved.Env);

        // Deploying needs the engine.
        await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(offline, "web", StackOperation.Sync));

        // Webhooks: one address per stack, found by it, turned off again.
        var secret = await _harness.Store.SetWebhookAsync(stack.Id, enabled: true, ct);
        repository = (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!;
        Assert.Equal(secret, _harness.Store.WebhookSecret(repository));
        Assert.Equal(stack.Id, (await _harness.Store.FindWebhookAsync(repository.WebhookId!, ct))!.Value.Stack.Id);
        await _harness.Store.SetWebhookAsync(stack.Id, enabled: false, ct);
        Assert.Null(await _harness.Store.FindWebhookAsync(repository.WebhookId!, ct));

        Assert.True(await _harness.Store.DisconnectRepositoryAsync(1, "web", ct));
        Assert.Null(await _harness.Store.GetRepositoryAsync(stack.Id, ct));
        Assert.False(Directory.Exists(checkout));
        Assert.NotNull(await _harness.Store.Files.ReadAsync(1, "web", ct));
        await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(offline, "web", StackOperation.Fetch));
    }

    [Fact]
    public async Task A_failed_fetch_is_recorded_and_removing_the_stack_removes_its_checkout()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.Commit("First", ("compose.yaml", Compose));
        var settings = new GitSettings(_repository.Url, "main", "compose.yaml", null, GitAuth.None, null, null, null, null);
        var staged = Path.Combine(_harness.Options.TempPath, "staged");
        var stack = await _harness.Store.CreateFromGitAsync(1, "api", StackKind.Compose, settings,
            await _harness.StackGit.FetchAsync(Request(composePath: "compose.yaml"), staged, null, null, ct), staged, null, ct);

        // The file moves away in the repository.
        _repository.Git("-C", _repository.Work, "rm", "--quiet", "compose.yaml");
        _repository.Commit("Moved");
        var offline = new EngineConnection(1, "local", DockerAddress.Parse("unix:///nonexistent/test.sock"), EndpointKind.Local);
        var job = await _harness.Jobs.StartAsync(offline, "api", StackOperation.Fetch);
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), ct))
        {
        }
        Assert.Contains("isn't in the repository", Assert.IsType<StackJobFailed>(job.Status).Error);
        var repository = (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!;
        Assert.Contains("isn't in the repository", repository.LastError);
        Assert.Contains("services:", (await _harness.Store.Files.ReadAsync(1, "api", ct))!.Compose);

        Assert.True(await _harness.Store.DeleteAsync(1, "api", ct));
        Assert.False(Directory.Exists(_harness.Store.CheckoutDirectory(1, "api")));
    }

    [Fact]
    public async Task Changing_the_address_drops_the_checkout_and_the_host_keys_of_another_host()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.Commit("First", ("compose.yaml", Compose));
        var settings = new GitSettings(_repository.Url, "main", "compose.yaml", null, GitAuth.None, null, null, null, null);
        var staged = Path.Combine(_harness.Options.TempPath, "staged");
        var stack = await _harness.Store.CreateFromGitAsync(1, "app", StackKind.Compose, settings,
            await _harness.StackGit.FetchAsync(Request(composePath: "compose.yaml"), staged, null, null, ct) with { KnownHosts = "old.host ssh-ed25519 AAAA\n" },
            staged, null, ct);
        Assert.NotNull((await _harness.Store.GetRepositoryAsync(stack.Id, ct))!.KnownHosts);

        await _harness.Store.SaveRepositoryAsync(1, "app", settings with { Url = "https://elsewhere.example/a/b.git" }, ct);
        var repository = (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!;
        Assert.Null(repository.KnownHosts);
        Assert.Null(repository.FetchedCommit);
        Assert.False(Directory.Exists(_harness.Store.CheckoutDirectory(1, "app")));

        // Switching to a token without one is refused; with one it's stored encrypted.
        await Assert.ThrowsAsync<StackRejectedException>(() => _harness.Store.SaveRepositoryAsync(1, "app", settings with { Auth = GitAuth.Token }, ct));
        await _harness.Store.SaveRepositoryAsync(1, "app", settings with { Auth = GitAuth.Token, Secret = "t0ken" }, ct);
        repository = (await _harness.Store.GetRepositoryAsync(stack.Id, ct))!;
        Assert.DoesNotContain("t0ken", repository.ProtectedSecret);
        Assert.Equal("t0ken", _harness.Store.Remote(repository).Secret);
        // Saving again without a token keeps it.
        await _harness.Store.SaveRepositoryAsync(1, "app", settings with { Auth = GitAuth.Token, PollMinutes = 5 }, ct);
        Assert.Equal("t0ken", _harness.Store.Remote((await _harness.Store.GetRepositoryAsync(stack.Id, ct))!).Secret);
    }
}
