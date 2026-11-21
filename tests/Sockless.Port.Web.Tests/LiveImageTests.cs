using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The image pages against a real engine, end to end through forms. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class LiveImageTests : IAsyncLifetime
{
    private const string Image = "busybox:1.36";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _repository = $"sockless-port-test/web-{Guid.NewGuid():n}"[..36];
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
        foreach (var tag in new[] { "tagged", "built" })
        {
            try
            {
                await _factory.LocalEndpoint.Client!.RemoveImageAsync($"{_repository}:{tag}", force: true);
            }
            catch (Exception ex) when (ex is DockerApiException or NullReferenceException)
            {
            }
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

    [Fact]
    public async Task Pull_inspect_tag_check_and_untag_through_the_pages()
    {
        var ct = TestContext.Current.CancellationToken;

        // The form's fallback without scripts pulls and waits. A tag no other test pulls: Docker's
        // containerd store fails one of two concurrent pulls of the same image ("lease does not exist").
        var pulled = await FollowAsync(await PostAsync("/endpoints/1/images", "/endpoints/1/images?handler=Pull", new() { ["reference"] = Image }));
        Assert.True(pulled.Contains($"Pulled {Image}.", StringComparison.Ordinal), Regex.Match(pulled, @"callout danger[\s\S]*?<p>([\s\S]*?)</p>").Groups[1].Value);

        var list = await _browser.GetStringAsync("/endpoints/1/images?q=busybox:1.36", ct);
        var id = ImageLink().Match(list).Groups[1].Value;
        Assert.NotEmpty(id);

        var detailUrl = $"/endpoints/1/images/{id}";
        var detail = await _browser.GetStringAsync(detailUrl, ct);
        Assert.Contains(Image, detail);
        Assert.Contains("Configuration", detail);
        Assert.Contains("Not checked", detail);
        Assert.Contains("BusyBox", await _browser.GetStringAsync($"{detailUrl}/history", ct));
        Assert.Contains("\"RepoDigests\"", WebUtility.HtmlDecode(await _browser.GetStringAsync($"{detailUrl}/inspect", ct)));

        // The registry still serves what was just pulled.
        var checkedPage = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=CheckUpdate", []));
        Assert.Contains(">Up to date<", checkedPage);

        var tagged = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=Tag", new() { ["reference"] = $"{_repository}:tagged" }));
        Assert.Contains($"{_repository}:tagged", tagged);

        var untagged = await FollowAsync(await PostAsync(detailUrl, $"{detailUrl}?handler=Untag", new() { ["tag"] = TagAsListed(tagged, $"{_repository}:tagged") }));
        Assert.Contains("Removed the tag", untagged);
        Assert.True(await _factory.LocalEndpoint.Client!.ImageExistsAsync(Image, ct));

        // "Create container" starts the form from the image.
        Assert.Contains($"value=\"{Image}\"", await _browser.GetStringAsync($"/endpoints/1/containers/create?image={Image}", ct));
    }

    [Fact]
    public async Task Build_from_a_dockerfile_runs_as_a_job_and_the_result_can_be_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = $"{_repository}:built";
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, "/endpoints/1/images/build");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(csrf), "__RequestVerificationToken" },
            { new StringContent("dockerfile"), "Input.Source" },
            // CRLF as a browser sends it from a textarea.
            { new StringContent("FROM busybox:1.37\r\nRUN echo web-build \\\r\n  > /marker\r\n"), "Input.Dockerfile" },
            { new StringContent(tag), "Input.Tags" },
        };

        var post = await _browser.PostAsync("/endpoints/1/images/build", form, ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var jobId = JobId().Match(post.Headers.Location!.OriginalString).Groups[1].Value;

        var job = _factory.Services.GetRequiredService<ImageJobs>().Get(jobId)!;
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(100), ct)) { }
        Assert.IsType<ImageJobSucceeded>(job.Status);
        var page = await _browser.GetStringAsync(post.Headers.Location.OriginalString, ct);
        Assert.Contains(">Done<", page);
        Assert.Contains("web-build", page);

        var (built, _) = await _factory.LocalEndpoint.Client!.InspectImageAsync(tag, ct);
        var removed = await FollowAsync(await PostAsync("/endpoints/1/images", "/endpoints/1/images?handler=Remove", new() { ["ids"] = built.Id[7..] }));
        Assert.Contains("Removed ", removed);
        Assert.False(await _factory.LocalEndpoint.Client!.ImageExistsAsync(tag, ct));
    }

    [Fact]
    public async Task Build_form_reports_missing_input()
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, "/endpoints/1/images/build");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(csrf), "__RequestVerificationToken" },
            { new StringContent("dockerfile"), "Input.Source" },
            { new StringContent("not a tag!"), "Input.Tags" },
        };

        var response = await _browser.PostAsync("/endpoints/1/images/build", form, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Write the Dockerfile", html);
        Assert.Contains("'tag!' isn't a valid name", WebUtility.HtmlDecode(html));
    }

    /// <summary>Podman lists tags fully qualified (<c>localhost/…</c> or <c>docker.io/…</c>).</summary>
    private static string TagAsListed(string html, string tag) =>
        Regex.Match(html, $"name=\"tag\" value=\"([^\"]*{Regex.Escape(tag)})\"").Groups[1].Value is { Length: > 0 } listed ? listed : tag;

    [GeneratedRegex("/endpoints/1/images/([0-9a-f]{64})\"")]
    private static partial Regex ImageLink();

    [GeneratedRegex("job=([0-9a-f]+)")]
    private static partial Regex JobId();
}
