using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>Adding, enrolling, renaming and removing agent endpoints, with no agent behind them.</summary>
public sealed partial class AgentPagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Browser, int Id)> AddAgentAsync(string name = "web-1", string address = "127.0.0.1:1")
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add");
        var response = await browser.PostAsync("/endpoints/add", PortFactory.Form(token, new() { ["Name"] = name, ["Address"] = address }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = int.Parse(SettingsPath().Match(response.Headers.Location!.OriginalString).Groups[1].Value);
        return (browser, id);
    }

    [Fact]
    public async Task Adding_an_agent_shows_its_install_command()
    {
        var (browser, id) = await AddAgentAsync();
        var connection = _factory.Services.GetRequiredService<EngineConnections>().Get(id)!;

        var html = await browser.GetStringAsync($"/endpoints/{id}/settings", Ct);

        Assert.Contains("Install the agent", html);
        Assert.Contains("docker run -d --name sockless-port-agent", html);
        Assert.Contains($"AGENT_TOKEN={connection.Enrolment!.Token.Reveal()}", html);
        Assert.Contains("-p 1:9001", html);
        Assert.Contains("Awaiting agent", html);

        var podman = await browser.GetStringAsync($"/endpoints/{id}/settings?variant=PodmanRootless", Ct);
        Assert.Contains("podman run -d --name sockless-port-agent", podman);

        var list = await browser.GetStringAsync("/endpoints", Ct);
        Assert.Contains("web-1", list);
        Assert.Contains("Awaiting agent", list);
    }

    [Fact]
    public async Task The_add_form_explains_what_is_wrong()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add");

        var response = await browser.PostAsync("/endpoints/add", PortFactory.Form(token, new() { ["Name"] = "local", ["Address"] = "not a host:99999" }), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("There&#x27;s already an endpoint called local.", html);
        Assert.Contains("The port must be a number from 1 to 65535.", html);
        Assert.Single(_factory.Services.GetRequiredService<EngineConnections>().All);
    }

    [Fact]
    public async Task Rename_reenrol_and_remove()
    {
        var (browser, id) = await AddAgentAsync();
        var connections = _factory.Services.GetRequiredService<EngineConnections>();
        var firstToken = connections.Get(id)!.Enrolment!.Token.Id;
        var settings = $"/endpoints/{id}/settings";

        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, settings);
        var renamed = await browser.PostAsync($"{settings}?handler=Rename", PortFactory.Form(token, new() { ["Name"] = "web-one" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);
        Assert.Equal("web-one", connections.Get(id)!.Name);

        token = await PortFactory.GetAntiforgeryTokenAsync(browser, settings);
        var reenrolled = await browser.PostAsync($"{settings}?handler=Reenrol", PortFactory.Form(token, new() { ["Address"] = "agent.example.com:9443" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, reenrolled.StatusCode);
        var connection = connections.Get(id)!;
        Assert.NotEqual(firstToken, connection.Enrolment!.Token.Id);
        Assert.Equal("https://agent.example.com:9443", connection.Address.ToString());

        token = await PortFactory.GetAntiforgeryTokenAsync(browser, settings);
        var removed = await browser.PostAsync($"{settings}?handler=Remove", PortFactory.Form(token, []), Ct);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Null(connections.Get(id));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(settings, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_local_endpoint_can_be_renamed_but_not_removed()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var settings = $"/endpoints/{_factory.LocalEndpoint.Id}/settings";

        var html = await browser.GetStringAsync(settings, Ct);
        Assert.DoesNotContain("Remove endpoint", html);
        Assert.DoesNotContain("New install command", html);

        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, settings);
        var removed = await browser.PostAsync($"{settings}?handler=Remove", PortFactory.Form(token, []), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        Assert.NotNull(_factory.LocalEndpoint);
    }

    [Fact]
    public async Task Adding_an_edge_agent_shows_a_command_that_dials_the_server()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var form = await browser.GetStringAsync("/endpoints/add?mode=edge", Ct);
        // Prefilled with the address the page was opened at.
        Assert.Contains("value=\"http://localhost\"", form);

        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add?mode=edge");
        var response = await browser.PostAsync("/endpoints/add?mode=edge",
            PortFactory.Form(token, new() { ["Name"] = "edge-1", ["ServerUrl"] = "https://port.example.com/" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = int.Parse(SettingsPath().Match(response.Headers.Location!.OriginalString).Groups[1].Value);
        var connection = _factory.Services.GetRequiredService<EngineConnections>().Get(id)!;
        Assert.True(connection.IsEdge);

        var html = await browser.GetStringAsync($"/endpoints/{id}/settings", Ct);
        Assert.Contains("AGENT_SERVER=https://port.example.com", html);
        Assert.Contains($"AGENT_TOKEN={connection.Enrolment!.Token.Reveal()}", html);
        Assert.DoesNotContain("-p ", html);
        Assert.Contains("Never connected", html);
        Assert.Contains("Sockless Port&#x27;s address", html);

        var list = await browser.GetStringAsync("/endpoints", Ct);
        Assert.Contains("Tunnel never connected", list);
    }

    [Fact]
    public async Task The_edge_form_needs_a_server_address()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/endpoints/add?mode=edge");

        var response = await browser.PostAsync("/endpoints/add?mode=edge", PortFactory.Form(token, new() { ["Name"] = "edge-1", ["ServerUrl"] = "port.example.com" }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("e.g. https://port.example.com.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(_factory.Services.GetRequiredService<EngineConnections>().All);
    }

    [Fact]
    public async Task The_tunnel_address_is_open_to_agents_only()
    {
        // No session needed (once set up), but a plain request isn't a tunnel.
        await _factory.CreateSignedInBrowserAsync();
        var anonymous = _factory.CreateBrowser();
        var response = await anonymous.GetAsync("/agent/tunnel?endpoint=1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("edge mode", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Renewing_needs_the_agent_online()
    {
        var (browser, id) = await AddAgentAsync();
        var settings = $"/endpoints/{id}/settings";
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, settings);

        var response = await browser.PostAsync($"{settings}?handler=Renew", PortFactory.Form(token, []), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Only an enrolled agent", await browser.GetStringAsync(settings, Ct));
    }

    [GeneratedRegex(@"/endpoints/(\d+)/settings")]
    private static partial Regex SettingsPath();
}
