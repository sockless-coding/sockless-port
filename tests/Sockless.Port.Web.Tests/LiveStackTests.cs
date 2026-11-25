using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// Stacks end to end through the forms against a real engine: create and deploy, edit, review and
/// redeploy, take down, remove. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set and Compose is installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class LiveStackTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"sp-web-{Guid.NewGuid():n}"[..15];
    private PortFactory _factory = null!;
    private HttpClient _browser = null!;

    private DockerClient Client => _factory.LocalEndpoint.Client!;

    private static string Compose(string greeting) =>
        $"services:\n  app:\n    image: {Image}\n    command: [\"sleep\", \"600\"]\n    network_mode: bridge\n    environment:\n      GREETING: \"{greeting}\"\n";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        _factory = new PortFactory(Address!);
        var (tool, problem) = await _factory.Services.GetRequiredService<ComposeCli>().StatusAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(tool is null, $"Compose isn't available: {problem}");
        _browser = await _factory.CreateSignedInBrowserAsync();
        await _factory.WaitForStateAsync<EndpointOnline>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is null)
            return;
        try
        {
            var compose = _factory.Services.GetRequiredService<ComposeCli>();
            await compose.RunAsync(new ComposeRequest(_name, ["down", "--volumes", "--remove-orphans"], _factory.LocalEndpoint.Address));
        }
        catch (Exception ex) when (ex is ComposeUnavailableException or IOException or NullReferenceException)
        {
        }
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> PostAsync(string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, formPage);
        return await _browser.PostAsync(action, PortFactory.Form(csrf, fields), TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the stack's current job to end and returns its whole log.</summary>
    private async Task<StackJobView> JobEndAsync()
    {
        var job = _factory.Services.GetRequiredService<StackJobs>().Latest(1, _name);
        Assert.NotNull(job);
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken))
        {
        }
        var view = job.View();
        Assert.True(view.Status is StackJobSucceeded, $"{view.Status}\n{string.Join('\n', view.Log)}");
        return view;
    }

    private async Task<string?> GreetingAsync()
    {
        var containers = await Client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"com.docker.compose.project={_name}"] }, all: true, TestContext.Current.CancellationToken);
        if (containers is not [var only])
            return null;
        var (inspect, _) = await Client.InspectContainerAsync(only.Id, TestContext.Current.CancellationToken);
        return inspect.Config?.Env?.FirstOrDefault(e => e.StartsWith("GREETING=", StringComparison.Ordinal))?["GREETING=".Length..];
    }

    [Fact]
    public async Task A_stack_is_deployed_reviewed_redeployed_and_removed_through_the_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        var detailUrl = $"/endpoints/1/stacks/{_name}";

        var created = await PostAsync("/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = _name, ["Input.Compose"] = Compose("hello"), ["deploy"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        await JobEndAsync();
        Assert.Equal("hello", await GreetingAsync());

        var detail = await _browser.GetStringAsync(detailUrl, ct);
        Assert.Contains($"Deployed {_name}.", detail);
        Assert.Contains($"{_name}-app-1", detail);
        Assert.Contains("Running 1/1", detail);
        Assert.Contains($">{_name}</a>", await _browser.GetStringAsync("/endpoints/1/stacks", ct));

        // Update check of the stack's containers (Podman before 6.2 can't, and offers none).
        if (ImageUpdates.CanCheck(_factory.LocalEndpoint))
        {
            Assert.Contains("been checked since Sockless Port started", detail);
            var check = await PostAsync(detailUrl, $"{detailUrl}?handler=CheckUpdates", []);
            Assert.Equal(HttpStatusCode.Redirect, check.StatusCode);
            Assert.Contains("Checked 1 container:", await _browser.GetStringAsync(check.Headers.Location!.OriginalString, ct));

            // As if the registry had moved on: the stack and its list row say so.
            var container = Assert.Single(await Client.ListContainersAsync(
                new Dictionary<string, string[]> { ["label"] = [$"com.docker.compose.project={_name}"] }, all: true, ct));
            await _factory.Services.GetRequiredService<ImageUpdates>().CheckAsync(_factory.LocalEndpoint, Client, container.ImageId!,
                ["busybox@sha256:" + new string('0', 64)], Image, ct);
            var outdated = await _browser.GetStringAsync($"{detailUrl}?handler=Overview", ct);
            Assert.Contains("The registry has a newer image for the service", outdated);
            Assert.Contains(">Update available<", outdated);
            Assert.Contains(">Update available<", await _browser.GetStringAsync($"/endpoints/1/stacks?q={_name}", ct));
        }
        else
        {
            Assert.Contains("Podman 6.2 and later can", detail);
            Assert.DoesNotContain("handler=CheckUpdates", detail);
        }

        // Scanning the stack's images queues its one image, and the table links to its scan.
        Assert.Contains("handler=ScanImages", detail);
        var scan = await PostAsync(detailUrl, $"{detailUrl}?handler=ScanImages", []);
        Assert.Equal(HttpStatusCode.Redirect, scan.StatusCode);
        Assert.Contains("Queued 1 scan.", await _browser.GetStringAsync(scan.Headers.Location!.OriginalString, ct));
        Assert.Contains("/vulnerabilities\"", await _browser.GetStringAsync($"{detailUrl}?handler=Overview", ct));

        // Edit, review the diff, deploy what was reviewed.
        var review = await PostAsync($"{detailUrl}/editor", $"{detailUrl}/editor?handler=Review", new()
        {
            ["Input.Compose"] = Compose("bonjour"), ["Input.Env"] = "",
        });
        var reviewHtml = await review.Content.ReadAsStringAsync(ct);
        Assert.Contains("1 added, 1 removed", reviewHtml);
        Assert.Contains("GREETING: &quot;bonjour&quot;", reviewHtml);
        var reviewed = ReviewedField().Match(reviewHtml).Groups[1].Value;

        var deploy = await PostAsync($"{detailUrl}/editor", $"{detailUrl}/editor?handler=Deploy", new()
        {
            ["Input.Compose"] = Compose("bonjour"), ["Input.Env"] = "", ["Reviewed"] = reviewed,
        });
        Assert.Equal(HttpStatusCode.Redirect, deploy.StatusCode);
        await JobEndAsync();
        Assert.Equal("bonjour", await GreetingAsync());
        Assert.DoesNotContain("differ from what was deployed", await _browser.GetStringAsync(detailUrl, ct));

        // Stop and take down through the action forms.
        var stop = await PostAsync(detailUrl, $"{detailUrl}?handler=Run", new() { ["operation"] = "stop" });
        Assert.Equal(HttpStatusCode.Redirect, stop.StatusCode);
        await JobEndAsync();
        Assert.Contains("Stopped", await _browser.GetStringAsync($"{detailUrl}?handler=Overview", ct));

        await PostAsync(detailUrl, $"{detailUrl}?handler=Run", new() { ["operation"] = "down" });
        await JobEndAsync();
        Assert.Null(await GreetingAsync());

        // Removed: the page still shows how the removal went, then the stack is gone from the list.
        await PostAsync(detailUrl, $"{detailUrl}?handler=Run", new() { ["operation"] = "remove" });
        await JobEndAsync();
        var gone = await _browser.GetStringAsync(detailUrl, ct);
        Assert.Contains($"{_name} is gone", gone);
        Assert.DoesNotContain($">{_name}</a>", await _browser.GetStringAsync("/endpoints/1/stacks", ct));
    }

    [Fact]
    public async Task A_project_started_elsewhere_is_listed_as_external_and_can_be_adopted()
    {
        var ct = TestContext.Current.CancellationToken;
        var compose = _factory.Services.GetRequiredService<ComposeCli>();
        var elsewhere = Directory.CreateTempSubdirectory("sp-web-elsewhere").FullName;
        try
        {
            File.WriteAllText(Path.Combine(elsewhere, "compose.yaml"), Compose("outside"));
            var up = await compose.RunAsync(new ComposeRequest(_name, ["up", "--detach"], _factory.LocalEndpoint.Address, elsewhere), cancellationToken: ct);
            Assert.True(up.Succeeded, string.Join('\n', up.Output));

            var list = await _browser.GetStringAsync($"/endpoints/1/stacks?q={_name}", ct);
            Assert.Contains("External", list);
            Assert.Contains($"href=\"/endpoints/1/stacks/create?adopt={_name}\"", list);
            Assert.Contains("deployed outside Sockless Port", await _browser.GetStringAsync($"/endpoints/1/stacks/{_name}", ct));

            var adoptForm = await _browser.GetStringAsync($"/endpoints/1/stacks/create?adopt={_name}", ct);
            Assert.Contains("GREETING: &quot;outside&quot;", adoptForm);

            var adopted = await PostAsync($"/endpoints/1/stacks/create?adopt={_name}", $"/endpoints/1/stacks/create?adopt={_name}", new()
            {
                ["Input.Compose"] = Compose("outside"), ["Input.Env"] = "", ["deploy"] = "false",
            });
            Assert.Equal(HttpStatusCode.Redirect, adopted.StatusCode);
            var detail = await _browser.GetStringAsync($"/endpoints/1/stacks/{_name}", ct);
            Assert.Contains($"Adopted {_name}.", detail);
            Assert.Contains("Adopted", detail);
            Assert.Equal("outside", await GreetingAsync());
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [GeneratedRegex("name=\"Reviewed\" value=\"([0-9a-f]+)\"")]
    private static partial Regex ReviewedField();
}
