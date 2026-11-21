using System.Net;

namespace Sockless.Port.Docker.Tests;

public sealed class DockerClientTests
{
    [Theory]
    [InlineData("1.56", "1.51")]
    [InlineData("1.51", "1.51")]
    [InlineData("1.44", "1.44")]
    [InlineData("1.41", "1.41")]
    public async Task Negotiates_the_highest_common_api_version(string server, string expected)
    {
        var handler = new FakeEngineHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/version" => FakeEngineHandler.Version(server),
            var path => FakeEngineHandler.Json($$"""{"path":"{{path}}"}"""),
        });

        using var client = await FakeEngineHandler.ConnectAsync(handler);
        await client.GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Version.Parse(expected), client.ApiVersion);
        Assert.Equal("/version", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal($"/v{expected}/info", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Engines_older_than_the_minimum_are_refused()
    {
        var handler = new FakeEngineHandler(_ => FakeEngineHandler.Version("1.40"));

        var error = await Assert.ThrowsAsync<DockerApiVersionException>(() => FakeEngineHandler.ConnectAsync(handler));
        Assert.Equal(new Version(1, 40), error.ServerVersion);
    }

    [Fact]
    public async Task Engine_errors_carry_status_and_message()
    {
        var handler = new FakeEngineHandler(r => r.RequestUri!.AbsolutePath == "/version"
            ? FakeEngineHandler.Version("1.51")
            : FakeEngineHandler.Json("""{"message":"get missing: no such volume"}""", HttpStatusCode.NotFound));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => client.RemoveVolumeAsync("missing", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.IsNotFound);
        Assert.Equal("get missing: no such volume", error.Message);
        Assert.Equal("/v1.51/volumes/missing", error.RequestPath);
    }

    [Fact]
    public async Task Non_json_errors_keep_the_body_text()
    {
        var handler = new FakeEngineHandler(r => r.RequestUri!.AbsolutePath == "/version"
            ? FakeEngineHandler.Version("1.51")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom\n") });
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => client.GetInfoAsync(TestContext.Current.CancellationToken));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task Query_values_and_names_are_escaped()
    {
        var handler = new FakeEngineHandler(r => r.RequestUri!.AbsolutePath == "/version"
            ? FakeEngineHandler.Version("1.51")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await client.RemoveVolumeAsync("odd name/..", force: true, TestContext.Current.CancellationToken);

        Assert.Equal("/v1.51/volumes/odd%20name%2F..?force=true", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Event_filters_are_sent_as_json_and_events_stream()
    {
        var handler = new FakeEngineHandler(r => r.RequestUri!.AbsolutePath == "/version"
            ? FakeEngineHandler.Version("1.51")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(Fixture.Path("docker-events.jsonl"))) });
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var count = 0;
        await foreach (var _ in client.MonitorEventsAsync(new Dictionary<string, string[]> { ["type"] = ["container", "volume"] }, TestContext.Current.CancellationToken))
            count++;

        Assert.Equal(13, count);
        Assert.Equal("""filters={"type":["container","volume"]}""", Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query.TrimStart('?')));
    }
}
