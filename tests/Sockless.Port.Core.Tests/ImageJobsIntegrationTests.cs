using System.Formats.Tar;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Registries;

namespace Sockless.Port.Core.Tests;

/// <summary>Background pulls and builds against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
public sealed class ImageJobsIntegrationTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly string _tag = $"sockless-port-test/job-{Guid.NewGuid():n}"[..36] + ":1";
    private ServiceProvider _services = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;
    private ImageJobs _jobs = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        var ct = TestContext.Current.CancellationToken;
        await _db.OpenAsync(ct);
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PortDbContext>().Database.MigrateAsync(ct);

        var address = DockerAddress.Parse(Address!);
        _client = await DockerClient.ConnectAsync(address, cancellationToken: ct);
        _connection = new EngineConnection(1, "test", address, isLocal: true);
        _connection.SetOnline(_client, EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(ct), _client.ApiVersion), DateTimeOffset.UtcNow);

        var registries = new RegistryCredentials(_services.GetRequiredService<IServiceScopeFactory>(), new EphemeralDataProtectionProvider(),
            TimeProvider.System, NullLogger<RegistryCredentials>.Instance);
        _jobs = new ImageJobs(registries, TimeProvider.System, NullLogger<ImageJobs>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try { await _client.RemoveImageAsync(_tag, force: true); } catch (DockerApiException) { }
        _jobs.Dispose();
        _client.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static async Task<ImageJobView> EndOfAsync(ImageJob job)
    {
        ImageJobView? last = null;
        await foreach (var view in job.WatchAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
            last = view;
        return last!;
    }

    [Fact]
    public async Task A_pull_runs_in_the_background_and_a_second_start_joins_it()
    {
        var job = _jobs.StartPull(_connection, "busybox:1.37");
        var same = _jobs.StartPull(_connection, "busybox:1.37");

        var end = await EndOfAsync(job);

        Assert.Same(job, same);
        Assert.IsType<ImageJobSucceeded>(end.Status);
        Assert.All(end.Pull.Layers, l => Assert.Equal(Docker.Streams.LayerPhase.Done, l.Phase));
        Assert.Contains(job, _jobs.ForEndpoint(1));
        Assert.True(await _client.ImageExistsAsync("busybox:1.37", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pull_of_a_present_image_can_finish_at_once()
    {
        await EndOfAsync(_jobs.StartPull(_connection, "busybox:1.37"));

        var end = await EndOfAsync(_jobs.StartPull(_connection, "busybox:1.37", onlyIfMissing: true));

        Assert.Contains("already on test", Assert.IsType<ImageJobSucceeded>(end.Status).Message);
        Assert.Empty(end.Pull.Layers);
    }

    [Fact]
    public async Task A_failed_pull_says_why()
    {
        var end = await EndOfAsync(_jobs.StartPull(_connection, "sockless-port/does-not-exist:never"));

        var failed = Assert.IsType<ImageJobFailed>(end.Status);
        Assert.Matches("(?i)denied|not found|does not exist|unauthorized", failed.Error);
    }

    [Fact]
    public async Task A_build_streams_its_log_and_deletes_the_context()
    {
        var context = Path.Combine(Path.GetTempPath(), $"sockless-port-test-{Guid.NewGuid():n}.tar");
        await using (var file = File.Create(context))
        await using (var tar = new TarWriter(file, TarEntryFormat.Ustar))
        {
            await tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("FROM busybox:1.37\nRUN echo job-build\n")),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            }, TestContext.Current.CancellationToken);
        }

        var job = _jobs.StartBuild(_connection, context, new ImageBuildOptions { Tags = [_tag] });
        var end = await EndOfAsync(job);

        var done = Assert.IsType<ImageJobSucceeded>(end.Status);
        Assert.StartsWith("sha256:", done.ImageId);
        Assert.Contains(job.View().Log, line => line.Contains("job-build", StringComparison.Ordinal));
        Assert.False(File.Exists(context));
    }

    [Fact]
    public async Task Cancelling_a_build_stops_it()
    {
        var context = Path.Combine(Path.GetTempPath(), $"sockless-port-test-{Guid.NewGuid():n}.tar");
        await using (var file = File.Create(context))
        await using (var tar = new TarWriter(file, TarEntryFormat.Ustar))
        {
            await tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("FROM busybox:1.37\nRUN sleep 60\n")),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            }, TestContext.Current.CancellationToken);
        }

        var job = _jobs.StartBuild(_connection, context, new ImageBuildOptions());
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        job.Cancel();
        var end = await EndOfAsync(job).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.IsType<ImageJobCanceled>(end.Status);
        Assert.False(File.Exists(context));
    }
}
