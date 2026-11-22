using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The container pages against a real engine, end to end through forms. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class LiveContainerTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sp-web-{Guid.NewGuid():n}"[..20];
    private PortFactory _factory = null!;
    private HttpClient _browser = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        _factory = new PortFactory(Address!);
        _browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOnline>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is null)
            return;
        try
        {
            await _factory.LocalEndpoint.Client!.RemoveContainerAsync(_name, force: true);
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

    [Fact]
    public async Task Create_list_act_and_remove_through_the_pages()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create (pulls busybox if needed) and start.
        var create = await PostAsync("/endpoints/1/containers/create", "/endpoints/1/containers/create", new()
        {
            ["Input.Image"] = "busybox:1.37",
            ["Input.Name"] = _name,
            ["Input.Command"] = "sh -c 'echo hello-from-port; sleep 300'",
            ["Input.Environment"] = "MARK=web-test",
            ["Input.Labels"] = "test=sockless-port",
            ["Input.RestartPolicy"] = "no",
            ["Pull"] = "missing",
            ["Start"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var detailUrl = create.Headers.Location!.OriginalString;
        var id = DetailId().Match(detailUrl).Groups[1].Value;

        var detail = await _browser.GetStringAsync(detailUrl, ct);
        Assert.Contains($"Created and started {_name}.", detail);
        Assert.Contains(">Running<", detail);

        // Listed, and filterable by name and label.
        var list = await _browser.GetStringAsync($"/endpoints/1/containers?q={_name}&label=test=sockless-port", ct);
        Assert.Contains($">{_name}</a>", list);
        Assert.DoesNotContain($">{_name}</a>", await _browser.GetStringAsync($"/endpoints/1/containers?q={_name}&state=paused", ct));

        // Logs download has the output.
        await Task.Delay(1000, ct);
        var log = await _browser.GetStringAsync($"/endpoints/1/containers/{id}/logs?handler=Download", ct);
        Assert.Contains("stdout hello-from-port", log);

        // Inspect shows the engine's JSON.
        Assert.Contains("MARK=web-test", await _browser.GetStringAsync($"/endpoints/1/containers/{id}/inspect", ct));

        // Update check from the detail page and the list: the registry is asked, nothing is pulled.
        var check = await PostAsync(detailUrl, $"{detailUrl}?handler=CheckUpdate", []);
        Assert.Equal(HttpStatusCode.Redirect, check.StatusCode);
        Assert.Matches(">(Up to date|Update available)<", await _browser.GetStringAsync(check.Headers.Location!.OriginalString, ct));
        var listCheck = await PostAsync("/endpoints/1/containers", $"/endpoints/1/containers?handler=CheckUpdates&q={_name}", []);
        Assert.Contains("Checked 1 container:", await _browser.GetStringAsync(listCheck.Headers.Location!.OriginalString, ct));

        // As if the registry had moved on: the list flags it and the detail page says how to update.
        var client = _factory.LocalEndpoint.Client!;
        var (inspected, _) = await client.InspectContainerAsync(id, ct);
        await _factory.Services.GetRequiredService<ImageUpdates>().CheckAsync(_factory.LocalEndpoint, client, inspected.Image!,
            ["busybox@sha256:" + new string('0', 64)], inspected.Config!.Image!, ct);
        Assert.Contains(">Update available<", await _browser.GetStringAsync($"/endpoints/1/containers?q={_name}", ct));
        Assert.Contains("The registry has a newer image for", await _browser.GetStringAsync(detailUrl, ct));

        // Bulk stop from the list.
        var stop = await PostAsync("/endpoints/1/containers", "/endpoints/1/containers?handler=Bulk", new() { ["action"] = "stop", ["ids"] = id });
        Assert.Equal(HttpStatusCode.Redirect, stop.StatusCode);
        Assert.Contains($"Stopped {_name}.", await _browser.GetStringAsync(stop.Headers.Location!.OriginalString, ct));

        // Recreate from the detail page gets a new id and keeps the name.
        var recreate = await PostAsync(detailUrl, $"{detailUrl}?handler=Recreate", new() { ["pull"] = "false" });
        Assert.Equal(HttpStatusCode.Redirect, recreate.StatusCode);
        var newId = DetailId().Match(recreate.Headers.Location!.OriginalString).Groups[1].Value;
        Assert.NotEqual(id, newId);
        Assert.Contains($"Recreated {_name}.", await _browser.GetStringAsync(recreate.Headers.Location.OriginalString, ct));

        // Duplicate prefills the create form from it.
        var duplicate = await _browser.GetStringAsync($"/endpoints/1/containers/create?from={newId}", ct);
        Assert.Contains($"value=\"{_name}-copy\"", duplicate);
        Assert.Contains("MARK=web-test", duplicate);

        // Remove.
        var remove = await PostAsync($"/endpoints/1/containers/{newId}", $"/endpoints/1/containers/{newId}?handler=Action", new() { ["action"] = "remove" });
        Assert.Equal("/endpoints/1/containers", remove.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.GetAsync($"/endpoints/1/containers/{newId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Create_form_reports_field_errors_and_engine_errors()
    {
        var invalid = await PostAsync("/endpoints/1/containers/create", "/endpoints/1/containers/create", new()
        {
            ["Input.Image"] = "busybox:1.37",
            ["Input.Ports"] = "80:99999",
            ["Input.RestartPolicy"] = "no",
        });
        var html = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("is not a port", html);
        Assert.Contains("aria-invalid=\"true\"", html);

        var missingImage = await PostAsync("/endpoints/1/containers/create", "/endpoints/1/containers/create", new()
        {
            ["Input.Image"] = "sockless-port/does-not-exist:never",
            ["Input.RestartPolicy"] = "no",
            ["Pull"] = "missing",
        });
        Assert.Equal(HttpStatusCode.OK, missingImage.StatusCode);
        Assert.Contains("callout danger", await missingImage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [GeneratedRegex("/containers/([0-9a-f]{64})")]
    private static partial Regex DetailId();
}
