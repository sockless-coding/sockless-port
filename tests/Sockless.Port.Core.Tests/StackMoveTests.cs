using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Moves;
using Sockless.Port.Stacks;
using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

/// <summary>Moving stacks between endpoints: what works without an engine.</summary>
public sealed class StackMoveTests : IAsyncLifetime
{
    private const string Compose = "services:\n  web:\n    image: nginx:1.27\n    volumes:\n      - data:/data\nvolumes:\n  data:\n";

    private StackHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await StackHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task A_copy_has_the_files_and_git_settings_and_the_webhook_moves_over_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var nas = await _harness.AddEndpointAsync("nas");
        await _harness.Store.CreateAsync(1, "web", new StackContent(Compose, "TAG=1\n"), adopted: true, StackKind.Compose, ct, template: "Nginx (Built-in)");
        await _harness.Store.SaveRepositoryAsync(1, "web",
            new GitSettings("https://git.example/team/web.git", "main", "compose.yaml", null, Port.Stacks.GitAuth.Token, "bot", "t0ken", null, 15), ct);
        var source = (await _harness.Store.GetAsync(1, "web", ct))!;
        var secret = await _harness.Store.SetWebhookAsync(source.Id, enabled: true, ct);
        var sourceRepository = (await _harness.Store.GetRepositoryAsync(source.Id, ct))!;

        var copy = await _harness.Store.CopyAsync(1, nas, "web", ct);

        Assert.Equal(nas, copy.EndpointId);
        Assert.True(copy.Adopted);
        Assert.Equal("Nginx (Built-in)", copy.Template);
        Assert.Null(copy.DeployedAt);
        Assert.Equal(new StackContent(Compose, "TAG=1\n"), await _harness.Store.Files.ReadAsync(nas, "web", ct));
        var copied = (await _harness.Store.GetRepositoryAsync(copy.Id, ct))!;
        Assert.Equal(sourceRepository.Url, copied.Url);
        Assert.Equal(sourceRepository.ProtectedSecret, copied.ProtectedSecret);
        Assert.Equal(15, copied.PollMinutes);
        Assert.Null(copied.WebhookId);                                 // one stack per address

        await Assert.ThrowsAsync<StackRejectedException>(() => _harness.Store.CopyAsync(1, nas, "web", ct));

        await _harness.Store.MoveWebhookAsync(1, nas, "web", ct);
        copied = (await _harness.Store.GetRepositoryAsync(copy.Id, ct))!;
        Assert.Equal(sourceRepository.WebhookId, copied.WebhookId);
        Assert.Equal(secret, _harness.Store.WebhookSecret(copied));
        Assert.Null((await _harness.Store.GetRepositoryAsync(source.Id, ct))!.WebhookId);
        Assert.Equal(copy.Id, (await _harness.Store.FindWebhookAsync(copied.WebhookId!, ct))!.Value.Stack.Id);
    }

    [Fact]
    public async Task Copying_a_stack_that_isnt_managed_is_refused()
    {
        var nas = await _harness.AddEndpointAsync("nas");

        await Assert.ThrowsAsync<StackRejectedException>(() => _harness.Store.CopyAsync(1, nas, "nothing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_move_keeps_both_stacks_busy_until_it_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Offline(1, "local");
        var target = Offline(2, "nas");
        var release = new TaskCompletionSource<StackJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = _harness.Jobs.StartMove(source, target, "web", _ => release.Task);

        Assert.Equal(StackOperation.Move, job.Operation);
        Assert.Equal(2, job.EndpointId);
        Assert.Equal(1, job.SourceEndpointId);
        Assert.True(_harness.Jobs.IsBusy(1, "web"));
        Assert.True(_harness.Jobs.IsBusy(2, "web"));
        Assert.Same(job, _harness.Jobs.Latest(1, "web"));
        Assert.Throws<StackJobRejectedException>(() => _harness.Jobs.StartMove(source, target, "web", _ => release.Task));
        await Assert.ThrowsAsync<StackJobRejectedException>(() => _harness.Jobs.StartAsync(source, "web", StackOperation.Deploy));

        release.SetResult(new StackJobSucceeded("Moved web to nas."));
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(20), ct))
        {
        }
        Assert.Equal("Moved web to nas.", Assert.IsType<StackJobSucceeded>(job.Status).Message);
        Assert.False(_harness.Jobs.IsBusy(1, "web"));
    }

    [Fact]
    public async Task A_move_that_throws_fails_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = _harness.Jobs.StartMove(Offline(1, "local"), Offline(2, "nas"), "web", _ => throw new IOException("The disk is full."));

        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(20), ct))
        {
        }
        Assert.Contains("The disk is full", Assert.IsType<StackJobFailed>(job.Status).Error);
    }

    [Fact]
    public async Task The_checks_refuse_a_stack_that_isnt_managed_and_offline_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var nas = await _harness.AddEndpointAsync("nas");
        var moves = Moves();

        var unmanaged = await moves.CheckAsync(Offline(1, "local"), Offline(nas, "nas"), "web", ct);
        Assert.False(unmanaged.CanMove);
        Assert.Contains(unmanaged.Findings, f => f.Severity == MoveSeverity.Blocker && f.Message.Contains("Adopt it first", StringComparison.Ordinal));

        await _harness.Store.CreateAsync(1, "web", new StackContent(Compose, ""), adopted: false, StackKind.Compose, ct);
        await _harness.Store.CreateAsync(nas, "web", new StackContent(Compose, ""), adopted: false, StackKind.Compose, ct);
        var offline = await moves.CheckAsync(Offline(1, "local"), Offline(nas, "nas"), "web", ct);
        Assert.False(offline.CanMove);
        Assert.Contains(offline.Findings, f => f.Message.Contains("There's already a stack named web on nas", StringComparison.Ordinal));
        Assert.Contains(offline.Findings, f => f.Message.Contains("local is offline", StringComparison.Ordinal));
        Assert.Equal([MoveSourceAfter.Remove, MoveSourceAfter.Stop, MoveSourceAfter.KeepRunning], offline.Afters);

        var same = await moves.CheckAsync(Offline(1, "local"), Offline(1, "local"), "web", ct);
        Assert.Contains(same.Findings, f => f.Message.Contains("is on local already", StringComparison.Ordinal));
        await Assert.ThrowsAsync<StackJobRejectedException>(() =>
            moves.StartAsync(Offline(1, "local"), Offline(nas, "nas"), "web", new MoveOptions(CopyVolumes: true, MoveSourceAfter.Remove), ct));
    }

    [Theory]
    [InlineData("x86_64", "amd64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("ARM64", "arm64")]
    [InlineData("riscv64", "riscv64")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Architectures_are_named_as_images_name_them(string? name, string? expected) =>
        Assert.Equal(expected, StackMoves.Architecture(name));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_500, "1.5 kB")]
    [InlineData(256_000_000, "256 MB")]
    [InlineData(1_234_000_000, "1.2 GB")]
    public void Sizes_read_like_the_engines(long bytes, string expected) => Assert.Equal(expected, StackMoves.Bytes(bytes));

    [Fact]
    public async Task A_counting_stream_counts_what_passes()
    {
        var seen = new List<long>();
        await using var counting = new CountingStream(new MemoryStream(new byte[10_000]), seen.Add);

        await counting.CopyToAsync(Stream.Null, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(10_000, counting.Count);
        Assert.Equal(10_000, seen[^1]);
    }

    private StackMoves Moves() => new(_harness.Store, _harness.Jobs, _harness.Compose, _harness.Docker,
        new VolumeBrowser(Options.Create(_harness.Options), _harness.Registries, TimeProvider.System, NullLogger<VolumeBrowser>.Instance),
        _harness.Registries, NullLogger<StackMoves>.Instance);

    private static EngineConnection Offline(int id, string name) =>
        new(id, name, DockerAddress.Parse($"unix:///nonexistent/{name}.sock"), EndpointKind.Local);
}
