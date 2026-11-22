using System.Net;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

/// <summary>Pod pages with no engine: they must explain, not fail.</summary>
public sealed class PodPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/endpoints/1/pods")]
    [InlineData("/endpoints/1/pods/web")]
    [InlineData("/endpoints/1/pods/web/inspect")]
    public async Task Pages_render_the_offline_explanation(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/endpoints/1/pods/-web")]
    [InlineData("/endpoints/1/pods/a%20b/inspect")]
    [InlineData("/endpoints/9/pods")]
    public async Task Invalid_names_and_endpoints_are_not_found(string path)
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Actions_on_an_offline_engine_change_nothing()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();
        var ct = TestContext.Current.CancellationToken;
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/1/containers/create");

        var post = await browser.PostAsync("/endpoints/1/pods?handler=Bulk&action=stop&ids=abc", PortFactory.Form(csrf, []), ct);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("nothing was changed", WebUtility.HtmlDecode(await browser.GetStringAsync(post.Headers.Location!.OriginalString, ct)));
    }

    [Fact]
    public void Pods_appear_only_on_engines_that_have_them()
    {
        var podman = new EngineDescriptor(EngineKind.Podman, "6.2.0", new Version(1, 41), "linux", "amd64", EngineCapabilities.Pods);
        var docker = podman with { Kind = EngineKind.Docker, Capabilities = EngineCapabilities.None };
        var pods = Assert.Single(Navigation.Main, i => i.Label == "Pods");

        Assert.True(pods.IsAvailable(podman));
        Assert.False(pods.IsAvailable(docker));
        Assert.False(pods.IsAvailable(null));
    }

    [Fact]
    public void Rootless_notes_show_only_on_rootless_engines()
    {
        var rootful = new EngineDescriptor(EngineKind.Podman, "6.2.0", new Version(1, 41), "linux", "amd64", EngineCapabilities.Pods, CgroupVersion: 2);
        var rootless = rootful with { Capabilities = EngineCapabilities.Pods | EngineCapabilities.Rootless };
        var rootlessV1 = rootless with { CgroupVersion = 1 };

        Assert.Null(RootlessNotes.Ports(rootful));
        Assert.Null(RootlessNotes.Limits(rootful));
        Assert.Null(RootlessNotes.Stats(rootful));
        Assert.Null(RootlessNotes.Ports(null));

        Assert.Contains("below 1024", RootlessNotes.Ports(rootless), StringComparison.Ordinal);
        Assert.Contains("delegates", RootlessNotes.Limits(rootless), StringComparison.Ordinal);
        Assert.Null(RootlessNotes.Stats(rootless));

        Assert.Contains("cgroups v1", RootlessNotes.Limits(rootlessV1), StringComparison.Ordinal);
        Assert.Contains("cgroups v1", RootlessNotes.Stats(rootlessV1), StringComparison.Ordinal);
    }

    [Fact]
    public void Pod_states_are_words_coloured_only_when_they_mean_something()
    {
        Assert.Equal(new StatusBadge("Running", "success"), PodStatus.State("Running"));
        Assert.Equal(new StatusBadge("Degraded", "warning"), PodStatus.State("Degraded"));
        Assert.Equal(new StatusBadge("Exited", ""), PodStatus.State("Exited"));
        Assert.Equal(new StatusBadge("Created", ""), PodStatus.State("Created"));
        Assert.Equal(new StatusBadge("Unknown", ""), PodStatus.State(null));
    }

    [Fact]
    public void Published_ports_come_from_the_infra_containers_bindings()
    {
        var infra = new Sockless.Port.Docker.Models.PodInfraConfig
        {
            PortBindings = new Dictionary<string, IReadOnlyList<Sockless.Port.Docker.Models.PortBinding>?>
            {
                ["80/tcp"] = [new() { HostIp = "0.0.0.0", HostPort = "8080" }, new() { HostIp = "::", HostPort = "8080" }],
                ["53/udp"] = [new() { HostIp = "127.0.0.1", HostPort = "5353" }],
                ["9000/tcp"] = null,
            },
        };

        Assert.Equal(["127.0.0.1:5353→53/udp", "8080→80/tcp"], PodStatus.PublishedPorts(infra));
        Assert.Empty(PodStatus.PublishedPorts(null));
    }
}
