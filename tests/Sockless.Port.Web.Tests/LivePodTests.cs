using System.Net;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The pod pages against a real engine, end to end through forms. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is set; the pod tests need Podman.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LivePodTests : IAsyncLifetime
{
    private const string Image = "docker.io/library/busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sp-webpod-{Guid.NewGuid():n}"[..20];
    private PortFactory _factory = null!;
    private HttpClient _browser = null!;

    private DockerClient Client => _factory.LocalEndpoint.Client!;

    private bool HasPods => _factory.LocalEndpoint.Engine?.Has(EngineCapabilities.Pods) == true;

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
        if (HasPods)
        {
            try
            {
                await Client.RemovePodAsync(_name);
            }
            catch (DockerApiException)
            {
            }
        }
        _factory.Dispose();
    }

    private async Task<string> PostAsync(string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, formPage);
        var response = await _browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return WebUtility.HtmlDecode(await _browser.GetStringAsync(response.Headers.Location!.OriginalString, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Engines_without_pods_explain_instead()
    {
        Assert.SkipWhen(HasPods, "The test engine has pods.");

        var html = WebUtility.HtmlDecode(await _browser.GetStringAsync("/endpoints/1/pods", TestContext.Current.CancellationToken));

        Assert.Contains("Pods are a Podman feature", html);
        Assert.DoesNotContain("href=\"/endpoints/1/pods\"", await _browser.GetStringAsync("/endpoints/1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_inspect_stop_start_and_remove_a_pod_through_the_pages()
    {
        Assert.SkipUnless(HasPods, "The test engine has no pods.");
        var ct = TestContext.Current.CancellationToken;
        if (!await Client.ImageExistsAsync(Image, ct))
        {
            await foreach (var _ in Client.PullImageAsync(Image, cancellationToken: ct)) { }
        }
        var podId = await Client.CreatePodAsync(new PodCreateRequest { Name = _name }, ct);
        var appId = await Client.CreatePodContainerAsync(new PodContainerCreateRequest { Name = $"{_name}-app", Image = Image, Pod = podId, Command = ["sleep", "300"] }, ct);
        await Client.StartPodAsync(podId, ct);

        // Navigation and the list, with the pod's container under it.
        var list = WebUtility.HtmlDecode(await _browser.GetStringAsync($"/endpoints/1/pods?q={_name}", ct));
        Assert.Contains("href=\"/endpoints/1/pods\"", list);
        Assert.Contains($">{_name}</a>", list);
        Assert.Contains($">{_name}-app</a>", list);
        Assert.Contains("1 of 1 running", list);

        // The containers list says which pod each container is in, and filters by it.
        var containers = WebUtility.HtmlDecode(await _browser.GetStringAsync($"/endpoints/1/containers?pod={podId}", ct));
        Assert.Contains($"Pod: {_name}", containers);
        Assert.Contains("Pod infra", containers);
        Assert.Contains($">{_name}-app</a>", containers);

        // The detail page lists the infra container last, and inspect shows libpod's JSON.
        var detailUrl = $"/endpoints/1/pods/{podId}";
        var detail = WebUtility.HtmlDecode(await _browser.GetStringAsync(detailUrl, ct));
        Assert.Contains(">Infra</span>", detail);
        Assert.Contains($"Remove pod {_name}?", detail);
        Assert.Contains("\"SharedNamespaces\"", WebUtility.HtmlDecode(await _browser.GetStringAsync($"{detailUrl}/inspect", ct)));

        // A container in a pod isn't offered recreate, and a post is refused.
        var container = WebUtility.HtmlDecode(await _browser.GetStringAsync($"/endpoints/1/containers/{appId}", ct));
        Assert.Contains($"Pod: {_name}", container);
        Assert.DoesNotContain("form=\"recreate-form\" class=\"btn-soft secondary\"", container);
        Assert.Contains("Recreate isn't offered", container);
        var refused = await PostAsync($"/endpoints/1/containers/{appId}", $"/endpoints/1/containers/{appId}?handler=Recreate", new() { ["pull"] = "false" });
        Assert.Contains("a recreated container would leave it", refused);

        var stopped = await PostAsync(detailUrl, $"{detailUrl}?handler=Action", new() { ["action"] = "stop" });
        Assert.Contains($"Stopped the pod {_name}.", stopped);
        Assert.False(PodStates.IsActive((await Client.InspectPodAsync(podId, ct)).Pod.State));

        var started = await PostAsync("/endpoints/1/pods", $"/endpoints/1/pods?handler=Bulk&action=start&ids={podId}", []);
        Assert.Contains($"Started {_name}.", started);
        Assert.Equal(PodStates.Running, (await Client.InspectPodAsync(podId, ct)).Pod.State);

        var removed = await PostAsync(detailUrl, $"{detailUrl}?handler=Action", new() { ["action"] = "remove" });
        Assert.Contains($"Removed the pod {_name}.", removed);
        Assert.DoesNotContain(await Client.ListPodsAsync(ct), p => p.Id == podId);
        Assert.DoesNotContain(await Client.ListContainersAsync(all: true, ct), c => c.Id == appId);
    }
}
