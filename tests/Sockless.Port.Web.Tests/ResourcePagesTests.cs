using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Settings;

namespace Sockless.Port.Web.Tests;

/// <summary>The endpoint's Resources page, with no engine.</summary>
public sealed class ResourcePagesTests : IDisposable
{
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Page_renders_the_offline_explanation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The engine is offline", html);
        Assert.DoesNotContain("data-resources ", html); // no live table without an engine
        Assert.Contains("data-trends ", html);           // but the history is still there
    }

    [Fact]
    public async Task History_is_json_for_the_chosen_period_even_while_offline()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<Sockless.Port.Engines.EndpointOffline>();

        var response = await browser.GetAsync("/endpoints/1/resources?handler=History&range=hour", TestContext.Current.CancellationToken);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("Hour", root.GetProperty("range").GetString());
        Assert.Equal(60, root.GetProperty("points").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("points")[0].GetProperty("cpu").ValueKind);
        Assert.Equal(0, root.GetProperty("containers").GetArrayLength());
        Assert.Contains("Last hour, since", root.GetProperty("period").GetString());
    }

    [Fact]
    public async Task A_period_the_setting_does_not_keep_falls_back_to_the_day()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var json = await browser.GetStringAsync("/endpoints/1/resources?handler=History&range=quarter", TestContext.Current.CancellationToken);

        Assert.Contains("\"range\":\"Day\"", json);
    }

    [Fact]
    public async Task With_history_off_the_page_says_so()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.Services.GetRequiredService<PortSettings>().SaveAsync(AppSettings.Default with { ResourceHistoryDays = 0 }, TestContext.Current.CancellationToken);

        var html = await browser.GetStringAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var json = await browser.GetStringAsync("/endpoints/1/resources?handler=History", TestContext.Current.CancellationToken);

        Assert.Contains("Resource history is off", html);
        Assert.Contains("href=\"/settings\"", html);
        Assert.DoesNotContain("data-trends ", html);
        Assert.Contains("\"enabled\":false", json);
    }

    [Fact]
    public async Task Resources_appear_in_the_navigation()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);

        Assert.Matches("aria-current=\"page\"[^>]*>\\s*<svg[^>]*>.*?</svg>\\s*<span>Resources</span>", System.Text.RegularExpressions.Regex.Replace(html, "\\s+", " ").Replace("> <", "><"));
    }

    [Fact]
    public async Task Unknown_endpoint_is_not_found()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var response = await browser.GetAsync("/endpoints/9/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_open_it_and_a_user_without_access_cannot()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "vera", "Correct-Horse-9", EndpointRole.Viewer);
        await PortFactory.CreateUserAsync(admin, "noel", "Correct-Horse-9", EndpointRole.None);
        var viewer = await _factory.SignInAsync("vera", "Correct-Horse-9");
        var outsider = await _factory.SignInAsync("noel", "Correct-Horse-9");

        var seen = await viewer.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var hidden = await outsider.GetAsync("/endpoints/1/resources", TestContext.Current.CancellationToken);
        var hiddenHistory = await outsider.GetAsync("/endpoints/1/resources?handler=History", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenHistory.StatusCode);
    }
}
