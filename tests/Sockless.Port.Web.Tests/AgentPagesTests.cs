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

    [GeneratedRegex(@"/endpoints/(\d+)/settings")]
    private static partial Regex SettingsPath();
}
