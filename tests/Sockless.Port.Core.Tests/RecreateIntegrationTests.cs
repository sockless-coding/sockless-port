using System.Net;
using System.Text;
using Sockless.Port.Containers;
using Sockless.Port.Docker;

namespace Sockless.Port.Core.Tests;

/// <summary>Create and recreate against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.</summary>
[Trait("Category", "Integration")]
public sealed class RecreateIntegrationTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private DockerClient _client = null!;
    private readonly List<string> _cleanup = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var name in _cleanup)
        {
            try { await _client.RemoveContainerAsync(name, force: true, removeVolumes: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    [Fact]
    public async Task Recreate_keeps_name_and_configuration_and_replaces_the_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"sp-recreate-{Guid.NewGuid():n}"[..24];
        _cleanup.Add(name);
        var (body, errors) = ContainerSpecBuilder.Build(new ContainerSpec
        {
            Image = "busybox:1.37",
            Command = "sleep 300",
            Environment = "MARK=kept",
            Labels = "test=sockless-port",
            RestartPolicy = "unless-stopped",
            MemoryLimit = 64,
            Volumes = "/scratch",
        });
        Assert.Empty(errors);
        if (!await _client.ImageExistsAsync("busybox:1.37", ct))
        {
            await foreach (var _ in _client.PullImageAsync("busybox:1.37", cancellationToken: ct)) { }
        }
        var original = (await _client.CreateContainerAsync(name, body!, ct)).Id;
        await _client.StartContainerAsync(original, ct);

        var replacement = await ContainerRecreator.RecreateAsync(_client, original, pull: true, ct);

        Assert.NotEqual(original, replacement);
        var (container, _) = await _client.InspectContainerAsync(replacement, ct);
        Assert.Equal(name, container.DisplayName);
        Assert.True(container.State!.Running);
        Assert.Contains("MARK=kept", container.Config!.Env!);
        Assert.Equal("sockless-port", container.Config.Labels!["test"]);
        Assert.Equal("unless-stopped", container.HostConfig!.RestartPolicy!.Name);
        Assert.Equal(64L * 1024 * 1024, container.HostConfig.Memory);
        var gone = await Assert.ThrowsAsync<DockerApiException>(() => _client.InspectContainerAsync(original, ct));
        Assert.True(gone.IsNotFound);
    }
}

/// <summary>Recreate's rollback, against a fake engine whose create fails.</summary>
public sealed class RecreateRollbackTests
{
    [Fact]
    public async Task Failed_create_renames_the_original_back_and_restarts_it()
    {
        var inspect = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker-container-inspect.json"));
        var calls = new List<string>();
        var handler = new Fake(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            calls.Add($"{request.Method} {path}{query}");
            return path switch
            {
                "/version" => Json("""{"ApiVersion":"1.51"}"""),
                var p when p.EndsWith("/json", StringComparison.Ordinal) => Json(inspect),
                var p when p.EndsWith("/containers/create", StringComparison.Ordinal) => Json("""{"message":"no space left on device"}""", HttpStatusCode.InternalServerError),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            };
        });
        using var client = await DockerClient.ConnectAsync(handler, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => ContainerRecreator.RecreateAsync(client, "fixture-web", pull: false, TestContext.Current.CancellationToken));

        Assert.Equal("no space left on device", error.Message);
        var id = calls.First(c => c.Contains("/stop", StringComparison.Ordinal)).Split('/')[3];
        Assert.Collection(calls.Skip(2), // version, inspect
            c => Assert.StartsWith($"POST /v1.51/containers/{id}/stop", c),
            c => Assert.StartsWith($"POST /v1.51/containers/{id}/rename?name=fixture-web-replaced-", c),
            c => Assert.Equal("POST /v1.51/containers/create?name=fixture-web", c),
            c => Assert.Equal($"POST /v1.51/containers/{id}/rename?name=fixture-web", c),
            c => Assert.Equal($"POST /v1.51/containers/{id}/start", c));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
