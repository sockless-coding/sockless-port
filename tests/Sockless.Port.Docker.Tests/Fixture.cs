using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sockless.Port.Docker.Tests;

internal static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Read(string name) => File.ReadAllText(Path(name));

    public static T Load<T>(string name, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(Read(name), typeInfo) ?? throw new InvalidDataException(name);
}

/// <summary>Answers requests from a routing function, recording each request.</summary>
internal sealed class FakeEngineHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Version(string apiVersion) =>
        Json($$"""{"Version":"29.0.0","ApiVersion":"{{apiVersion}}","Os":"linux","Arch":"amd64"}""");

    public static Task<DockerClient> ConnectAsync(FakeEngineHandler handler) =>
        DockerClient.ConnectAsync(handler, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, TestContext.Current.CancellationToken);
}
