using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The volume and network pages against a real engine, end to end through forms. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class LiveVolumeNetworkTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sp-web-{Guid.NewGuid():n}"[..20];
    private readonly List<string> _containers = [];
    private PortFactory _factory = null!;
    private HttpClient _browser = null!;

    private DockerClient Client => _factory.LocalEndpoint.Client!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        _factory = new PortFactory(Address!);
        _browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOnline>();
        if (!await Client.ImageExistsAsync(Image, TestContext.Current.CancellationToken))
        {
            await foreach (var _ in Client.PullImageAsync(Image, cancellationToken: TestContext.Current.CancellationToken)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is null)
            return;
        try
        {
            foreach (var id in _containers)
                await Client.RemoveContainerAsync(id, force: true);
            foreach (var helper in await Client.ListContainersAsync(new Dictionary<string, string[]> { ["label"] = [$"sockless-port.volume={_name}"] }))
                await Client.RemoveContainerAsync(helper.Id, force: true);
            await Client.RemoveVolumeAsync(_name, force: true);
        }
        catch (Exception ex) when (ex is DockerApiException or NullReferenceException)
        {
        }
        try
        {
            await Client.RemoveNetworkAsync(_name);
        }
        catch (Exception ex) when (ex is DockerApiException or NullReferenceException)
        {
        }
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> PostAsync(string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, formPage);
        return await _browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    private async Task<string> FollowAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await _browser.GetStringAsync(response.Headers.Location!.OriginalString, TestContext.Current.CancellationToken);
    }

    private async Task<string> RunAsync(string command, string? volume = null, string network = "bridge")
    {
        var host = new JsonObject { ["NetworkMode"] = network };
        if (volume is not null)
            host["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume", ["Source"] = volume, ["Target"] = "/data" });
        var created = await Client.CreateContainerAsync($"{_name}-{_containers.Count}", new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray("sh", "-c", command),
            ["HostConfig"] = host,
        }, TestContext.Current.CancellationToken);
        _containers.Add(created.Id);
        await Client.StartContainerAsync(created.Id, TestContext.Current.CancellationToken);
        return created.Id;
    }

    [Fact]
    public async Task Create_browse_download_and_remove_a_volume_through_the_pages()
    {
        var ct = TestContext.Current.CancellationToken;

        var detail = await FollowAsync(await PostAsync("/endpoints/1/volumes/create", "/endpoints/1/volumes/create", new()
        {
            ["Input.Name"] = _name,
            ["Input.Driver"] = "local",
            ["Input.Labels"] = "test=sockless-port",
        }));
        Assert.Contains($"Created the volume {_name}.", detail);
        Assert.Contains("test", detail);

        // A second create with the same name is refused on the field, not silently reused.
        var again = await PostAsync("/endpoints/1/volumes/create", "/endpoints/1/volumes/create", new() { ["Input.Name"] = _name, ["Input.Driver"] = "local" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains($"A volume named {_name} already exists.", await again.Content.ReadAsStringAsync(ct));

        // A container writes a file and stays, so the volume is in use.
        var writer = await RunAsync("mkdir -p /data/conf && echo hello-volume > /data/conf/app.ini && sleep 300", _name);
        var list = await _browser.GetStringAsync($"/endpoints/1/volumes?q={_name}&usage=used", ct);
        Assert.Contains($">{_name}</a>", list);
        await Task.Delay(500, ct);

        var detailUrl = $"/endpoints/1/volumes/{_name}";
        Assert.Contains($"{_name}-0", await _browser.GetStringAsync(detailUrl, ct));
        Assert.Contains("\"Mountpoint\"", WebUtility.HtmlDecode(await _browser.GetStringAsync($"{detailUrl}/inspect", ct)));
        Assert.Matches(@"\d+ B", await _browser.GetStringAsync($"{detailUrl}?handler=Size", ct));

        // Files: closed until opened with a POST.
        Assert.Contains("The file browser isn't open", await _browser.GetStringAsync($"{detailUrl}/files", ct));
        var root = await FollowAsync(await PostAsync($"{detailUrl}/files", $"{detailUrl}/files?handler=Open", new() { ["path"] = "/" }));
        Assert.Contains(">conf/<", root);
        var conf = await _browser.GetStringAsync($"{detailUrl}/files?path=/conf", ct);
        Assert.Contains(">app.ini<", conf);

        var download = await _browser.GetAsync($"{detailUrl}/files?handler=Download&path=/conf/app.ini", ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("hello-volume\n", await download.Content.ReadAsStringAsync(ct));
        Assert.Equal("app.ini", download.Content.Headers.ContentDisposition!.FileNameStar);
        var archive = await _browser.GetAsync($"{detailUrl}/files?handler=Archive&path=/conf", ct);
        Assert.Equal("application/x-tar", archive.Content.Headers.ContentType!.MediaType);

        // Editing: the file opens in the editor, saves while the writer uses the volume, and a stale edit is refused.
        var fileUrl = $"{detailUrl}/files?path=/conf/app.ini";
        var opened = WebUtility.HtmlDecode(await _browser.GetStringAsync(fileUrl, ct));
        Assert.Contains("hello-volume\n</textarea>", opened);
        var version = System.Text.RegularExpressions.Regex.Match(opened, "name=\"version\" value=\"([0-9a-f]{64})\"").Groups[1].Value;
        // Browsers send a text area's lines with CRLF; the file had LF, and keeps it.
        var saved = await FollowAsync(await PostAsync(fileUrl, $"{detailUrl}/files?handler=Save",
            new() { ["path"] = "/conf/app.ini", ["content"] = "edited\r\n", ["version"] = version }));
        Assert.Contains("Saved /conf/app.ini.", saved);
        Assert.Equal("edited\n", await (await _browser.GetAsync($"{detailUrl}/files?handler=Download&path=/conf/app.ini", ct)).Content.ReadAsStringAsync(ct));
        var stale = await PostAsync(fileUrl, $"{detailUrl}/files?handler=Save", new() { ["path"] = "/conf/app.ini", ["content"] = "mine", ["version"] = version });
        var conflict = WebUtility.HtmlDecode(await stale.Content.ReadAsStringAsync(ct));
        Assert.Contains("changed after you opened it", conflict);
        Assert.Contains("Save anyway", conflict);
        Assert.Contains("mine</textarea>", conflict);

        var outside = await _browser.GetStringAsync($"{detailUrl}/files?path=/conf/../..", ct);
        Assert.Contains("That path leaves the volume.", outside);

        // The browser's helper doesn't count as a user, and is listed as a helper.
        Assert.Contains("File browser helper", await _browser.GetStringAsync("/endpoints/1/containers", ct));
        Assert.Contains("Used by 1", await _browser.GetStringAsync(detailUrl, ct));

        // Removing while the writer uses it is refused with a plain reason.
        var refused = await FollowAsync(await PostAsync("/endpoints/1/volumes", "/endpoints/1/volumes?handler=Remove", new() { ["ids"] = _name }));
        Assert.Contains("a container uses it", refused);

        await Client.RemoveContainerAsync(writer, force: true, cancellationToken: ct);
        // The open file browser would block this; removal releases it first.
        var removed = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=Remove", []));
        Assert.Contains($"Removed the volume {_name}.", removed);
        Assert.Empty(await Client.ListContainersAsync(new Dictionary<string, string[]> { ["label"] = [$"sockless-port.volume={_name}"] }, cancellationToken: ct));
    }

    [Fact]
    public async Task Create_connect_disconnect_and_remove_a_network_through_the_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        var third = Random.Shared.Next(0, 255);

        var invalid = await PostAsync("/endpoints/1/networks/create", "/endpoints/1/networks/create", new()
        {
            ["Input.Name"] = _name,
            ["Input.Driver"] = "bridge",
            ["Input.Subnet"] = $"10.199.{third}.7/24",
        });
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains($"Use the network&#x27;s first address: 10.199.{third}.0/24.", await invalid.Content.ReadAsStringAsync(ct));

        var created = await PostAsync("/endpoints/1/networks/create", "/endpoints/1/networks/create", new()
        {
            ["Input.Name"] = _name,
            ["Input.Driver"] = "bridge",
            ["Input.Subnet"] = $"10.199.{third}.0/24",
            ["Input.Gateway"] = $"10.199.{third}.1",
            ["Input.Labels"] = "test=sockless-port",
        });
        var detailUrl = created.Headers.Location!.OriginalString;
        var detail = await FollowAsync(created);
        Assert.Contains($"Created the network {_name}.", detail);
        Assert.Contains($"10.199.{third}.0/24", detail);
        Assert.Contains("No container is attached", detail);

        var container = await RunAsync("sleep 300");
        var connected = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=Connect", new()
        {
            ["container"] = container,
            ["aliases"] = "db primary",
            ["address"] = $"10.199.{third}.50",
        }));
        Assert.True(connected.Contains($"Connected {_name}-0 to {_name}.", StringComparison.Ordinal), Regex.Match(connected, @"callout danger[\s\S]*?<p>([\s\S]*?)</p>").Groups[1].Value);
        Assert.Contains($"10.199.{third}.50/24", connected);

        var list = await _browser.GetStringAsync($"/endpoints/1/networks?q={_name}", ct);
        Assert.Contains($"{_name}-0", list);

        var disconnected = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=Disconnect", new() { ["container"] = container }));
        Assert.Contains($"Disconnected {_name}-0 from {_name}.", disconnected);

        var id = NetworkId().Match(detailUrl).Groups[1].Value;
        var removed = await FollowAsync(await PostAsync("/endpoints/1/networks", "/endpoints/1/networks?handler=Remove", new() { ["ids"] = id }));
        Assert.Contains($"Removed {_name}.", removed);
    }

    [GeneratedRegex("/endpoints/1/networks/([^/?]+)")]
    private static partial Regex NetworkId();
}
