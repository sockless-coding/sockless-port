using System.Formats.Tar;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sockless.Port.Containers;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

/// <summary>The volume file browser against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
public sealed class VolumeBrowserIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly string _volume = $"sp-test-browse-{Guid.NewGuid():n}"[..28];
    private ServiceProvider _services = null!;
    private DockerClient _client = null!;
    private EngineConnection _connection = null!;
    private VolumeBrowser _browser = null!;

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
        _connection = new EngineConnection(1, "test", address, EndpointKind.Local);
        _connection.SetOnline(_client, EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(ct), _client.ApiVersion), DateTimeOffset.UtcNow);

        var registries = new RegistryCredentials(_services.GetRequiredService<IServiceScopeFactory>(), new EphemeralDataProtectionProvider(),
            TimeProvider.System, NullLogger<RegistryCredentials>.Instance);
        _browser = new VolumeBrowser(Options.Create(new PortOptions { HelperImage = Image }), registries, TimeProvider.System, NullLogger<VolumeBrowser>.Instance);

        // Fill the volume through an ordinary container, which is then removed.
        await _client.CreateVolumeAsync(new VolumeCreateRequest { Name = _volume }, ct);
        if (!await _client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
        var writer = await _client.CreateContainerAsync(null, new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray("sh", "-c", "mkdir -p /v/conf/deep && echo hello > /v/hello.txt && echo x > '/v/conf/a|b.ini' && ln -s hello.txt /v/link && ln -s / /v/escape" +
                " && printf 'a: 1\\r\\nb: 2\\r\\n' > /v/conf/app.yml && chown 65534:65534 /v/conf/app.yml && chmod 640 /v/conf/app.yml" +
                " && printf 'a\\0b' > /v/blob.bin && touch /v/empty"),
            ["HostConfig"] = new JsonObject { ["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume", ["Source"] = _volume, ["Target"] = "/v" }) },
        }, ct);
        await _client.StartContainerAsync(writer.Id, ct);
        for (var i = 0; i < 100 && (await _client.InspectContainerAsync(writer.Id, ct)).Container.State!.Running; i++)
            await Task.Delay(100, ct);
        await _client.RemoveContainerAsync(writer.Id, force: true, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try { await _browser.ReleaseAsync(_connection, _client, _volume); } catch (DockerApiException) { }
        await _browser.DisposeAsync();
        try { await _client.RemoveVolumeAsync(_volume, force: true); } catch (DockerApiException) { }
        _client.Dispose();
        await _services.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<IReadOnlyList<ContainerSummary>> HelpersAsync() =>
        await _client.ListContainersAsync(new Dictionary<string, string[]> { ["label"] = [$"{VolumeBrowser.VolumeLabel}={_volume}"] }, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Lists_downloads_and_releases_through_one_read_only_helper()
    {
        var ct = TestContext.Current.CancellationToken;

        // Reading needs an open browser: a GET never starts a container.
        Assert.False(_browser.IsOpen(_connection.Id, _volume));
        await Assert.ThrowsAsync<VolumeBrowserClosedException>(() => _browser.ListAsync(_connection, _client, _volume, "/", ct));

        await _browser.OpenAsync(_connection, _client, _volume, ct);
        var root = await _browser.ListAsync(_connection, _client, _volume, "/", ct);
        var conf = await _browser.ListAsync(_connection, _client, _volume, "/conf", ct);
        var file = await _browser.ListAsync(_connection, _client, _volume, "/hello.txt", ct);
        var link = await _browser.ListAsync(_connection, _client, _volume, "/escape", ct);
        await _browser.OpenAsync(_connection, _client, _volume, ct);

        Assert.Equal(VolumeEntryKind.Directory, root.Self.Kind);
        Assert.Equal(["conf", "blob.bin", "empty", "escape", "hello.txt", "link"], root.Entries.Select(e => e.Name));
        Assert.Equal(VolumeEntryKind.Directory, root.Entries[0].Kind);
        Assert.Equal(VolumeEntryKind.Link, root.Entries.Single(e => e.Name == "escape").Kind);
        Assert.Equal(["deep", "app.yml", "a|b.ini"], conf.Entries.Select(e => e.Name));
        Assert.Equal((VolumeEntryKind.File, 6L), (file.Self.Kind, file.Self.Size));
        Assert.Empty(file.Entries);
        // A link to / is described, not followed.
        Assert.Equal(VolumeEntryKind.Link, link.Self.Kind);
        Assert.Empty(link.Entries);

        // One helper, reused, with the volume read-only and no network; it doesn't count as using the volume.
        var helper = Assert.Single(await HelpersAsync());
        var (inspect, _) = await _client.InspectContainerAsync(helper.Id, ct);
        Assert.False(Assert.Single(inspect.Mounts!).ReadWrite);
        Assert.Equal("none", inspect.HostConfig!.NetworkMode);
        Assert.Empty(VolumeUsage.ByVolume(await _client.ListContainersAsync(all: true, ct))[_volume]);
        Assert.True(ContainerLabels.IsHelper(helper.Labels));

        var missing = await Assert.ThrowsAsync<VolumeBrowseException>(() => _browser.ListAsync(_connection, _client, _volume, "/nope", ct));
        Assert.Contains("doesn't exist", missing.Message);

        string? downloaded = null;
        await _browser.DownloadFileAsync(_connection, _client, _volume, "/hello.txt", async (entry, data) =>
        {
            Assert.Equal(("hello.txt", 6L), (entry.Name, entry.Size));
            downloaded = await new StreamReader(data).ReadToEndAsync(ct);
        }, ct);
        Assert.Equal("hello\n", downloaded);
        await Assert.ThrowsAsync<VolumeBrowseException>(() => _browser.DownloadFileAsync(_connection, _client, _volume, "/link", (_, _) => Task.CompletedTask, ct));

        var names = new List<string>();
        await _browser.DownloadArchiveAsync(_connection, _client, _volume, "/conf", async archive =>
        {
            var tar = new TarReader(archive);
            while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                names.Add(entry.Name.TrimEnd('/'));
        }, ct);
        Assert.Contains("conf/a|b.ini", names);

        // Released before removing the volume, which would otherwise be in use.
        await _browser.ReleaseAsync(_connection, _client, _volume, ct);
        Assert.Empty(await HelpersAsync());
        Assert.False(_browser.IsOpen(_connection.Id, _volume));
        await _client.RemoveVolumeAsync(_volume, cancellationToken: ct);
    }

    [Fact]
    public async Task Saves_text_files_through_a_writable_helper_of_their_own()
    {
        var ct = TestContext.Current.CancellationToken;
        await _browser.OpenAsync(_connection, _client, _volume, ct);
        var readHelper = Assert.Single(await HelpersAsync()).Id;

        var file = (await _browser.ListAsync(_connection, _client, _volume, "/conf/app.yml", ct)).Self;
        var opened = await _browser.ReadTextAsync(_connection, _client, _volume, file, ct);
        Assert.Equal("a: 1\r\nb: 2\r\n", opened.Text);
        Assert.Contains("binary", (await _browser.ReadTextAsync(_connection, _client, _volume,
            (await _browser.ListAsync(_connection, _client, _volume, "/blob.bin", ct)).Self, ct)).NotEditable);

        // The browser sends CRLF; the file keeps its CRLF, owner and mode.
        await _browser.SaveTextAsync(_connection, _client, _volume, "/conf/app.yml", "a: 1\r\nb: 3\r\nc: 4", opened.Version, ct);
        var saved = (await _browser.ListAsync(_connection, _client, _volume, "/conf/app.yml", ct)).Self;
        Assert.Equal(("640", "nobody"), (saved.Mode, saved.Owner));
        var reread = await _browser.ReadTextAsync(_connection, _client, _volume, saved, ct);
        Assert.Equal("a: 1\r\nb: 3\r\nc: 4", reread.Text);

        // An edit of the old version doesn't overwrite the new one, unless asked to.
        await Assert.ThrowsAsync<VolumeFileChangedException>(() =>
            _browser.SaveTextAsync(_connection, _client, _volume, "/conf/app.yml", "stale", opened.Version, ct));
        await _browser.SaveTextAsync(_connection, _client, _volume, "/conf/app.yml", "forced\n", version: null, ct);
        Assert.Equal("forced\r\n", (await _browser.ReadTextAsync(_connection, _client, _volume,
            (await _browser.ListAsync(_connection, _client, _volume, "/conf/app.yml", ct)).Self, ct)).Text);

        // Empty files can be filled; links and missing files are refused, and the link's target is untouched.
        var empty = (await _browser.ListAsync(_connection, _client, _volume, "/empty", ct)).Self;
        var emptyOpened = await _browser.ReadTextAsync(_connection, _client, _volume, empty, ct);
        Assert.Equal("", emptyOpened.Text);
        await _browser.SaveTextAsync(_connection, _client, _volume, "/empty", "filled", emptyOpened.Version, ct);
        Assert.Equal(6, (await _browser.ListAsync(_connection, _client, _volume, "/empty", ct)).Self.Size);
        await Assert.ThrowsAsync<VolumeBrowseException>(() => _browser.SaveTextAsync(_connection, _client, _volume, "/link", "x", null, ct));
        await Assert.ThrowsAsync<VolumeBrowseException>(() => _browser.SaveTextAsync(_connection, _client, _volume, "/nope", "x", null, ct));
        Assert.Equal(6, (await _browser.ListAsync(_connection, _client, _volume, "/hello.txt", ct)).Self.Size);

        // Each save's helper is gone afterwards; the read-only browsing helper is still the one.
        Assert.Equal(readHelper, Assert.Single(await HelpersAsync()).Id);
    }

    [Fact]
    public async Task A_helper_removed_behind_its_back_closes_the_browser()
    {
        var ct = TestContext.Current.CancellationToken;
        await _browser.OpenAsync(_connection, _client, _volume, ct);
        await _client.RemoveContainerAsync(Assert.Single(await HelpersAsync()).Id, force: true, cancellationToken: ct);

        await Assert.ThrowsAsync<VolumeBrowserClosedException>(() => _browser.ListAsync(_connection, _client, _volume, "/", ct));
        Assert.False(_browser.IsOpen(_connection.Id, _volume));

        // Opening again starts a new one.
        await _browser.OpenAsync(_connection, _client, _volume, ct);
        Assert.NotEmpty((await _browser.ListAsync(_connection, _client, _volume, "/", ct)).Entries);
    }
}
