using System.Net;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Tests;

/// <summary>Pod models against captured libpod responses, and the requests the client sends.</summary>
public sealed class PodUnitTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    private static FakeEngineHandler Engine(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(r => r.RequestUri!.AbsolutePath == "/version" ? FakeEngineHandler.Version("1.41") : respond(r));

    [Fact]
    public void Pod_list_names_each_pods_containers_and_infra()
    {
        var pods = Fixture.Load("podman-pods.json", Json.ListPodSummary);

        Assert.Equal(["fixture-broken", "fixture-empty", "fixture-pod", "fixture-stopped"], pods.Select(p => p.Name).Order());

        var pod = Assert.Single(pods, p => p.Name == "fixture-pod");
        Assert.Equal(PodStates.Running, pod.Status);
        Assert.Equal("infra", pod.Labels!["team"]);
        Assert.Equal(3, pod.Containers!.Count);
        Assert.Equal(["fixture-pod-side", "fixture-pod-web"], pod.Workloads.Select(c => c.DisplayName).Order());
        Assert.True(pod.IsInfra(pod.InfraId!));
        Assert.All(pod.Containers, c => Assert.Equal("running", c.Status));

        Assert.Equal(PodStates.Degraded, Assert.Single(pods, p => p.Name == "fixture-broken").Status);
        Assert.Equal(PodStates.Created, Assert.Single(pods, p => p.Name == "fixture-stopped").Status);

        var empty = Assert.Single(pods, p => p.Name == "fixture-empty");
        Assert.Empty(empty.Containers!);
        Assert.Equal("", empty.InfraId);
        Assert.False(empty.IsInfra(""));
    }

    [Fact]
    public void Pod_inspect_is_read()
    {
        var pod = Fixture.Load("podman-pod-inspect.json", Json.PodInspect);

        Assert.Equal("fixture-pod", pod.Name);
        Assert.Equal(PodStates.Running, pod.State);
        Assert.True(pod.CreateInfra);
        Assert.Equal(["ipc", "net", "uts"], pod.SharedNamespaces);
        Assert.Equal("continue", pod.ExitPolicy);
        Assert.Equal(3, pod.NumContainers);
        Assert.Equal(["podman", "pod", "create", "--name", "fixture-pod", "--label", "team=infra"], pod.CreateCommand);
        // Nested Podman has host networking only.
        Assert.True(pod.InfraConfig!.HostNetwork);
        Assert.Single(pod.Containers!, c => pod.IsInfra(c.Id));
    }

    [Fact]
    public void Pod_inspect_of_a_degraded_pod_shows_which_container_is_down()
    {
        var pod = Fixture.Load("podman-pod-inspect-degraded.json", Json.PodInspect);

        Assert.Equal(PodStates.Degraded, pod.State);
        Assert.Equal("created", Assert.Single(pod.Containers!, c => c.Name == "fixture-broken-app").State);
        Assert.Equal("running", Assert.Single(pod.Containers!, c => pod.IsInfra(c.Id)).State);
    }

    [Fact]
    public async Task Requests_go_to_libpod_not_the_compatible_api()
    {
        var handler = Engine(r => FakeEngineHandler.Json(r.RequestUri!.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
            ? Fixture.Read("podman-pods.json")
            : Fixture.Read("podman-pod-rm.json")));
        using var client = await FakeEngineHandler.ConnectAsync(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.ListPodsAsync(ct);
        await client.StopPodAsync("fixture-pod", timeout: 3, cancellationToken: ct);
        await client.RemovePodAsync("fixture-pod", ct);

        Assert.Equal("/v4.0.0/libpod/pods/json", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal("/v4.0.0/libpod/pods/fixture-pod/stop?t=3", handler.Requests[2].RequestUri!.PathAndQuery);
        // Always forced: an unforced remove of a running pod fails only after removing its infra container.
        Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);
        Assert.Equal("/v4.0.0/libpod/pods/fixture-pod?force=true", handler.Requests[3].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Starting_or_stopping_a_pod_already_in_that_state_succeeds()
    {
        using var client = await FakeEngineHandler.ConnectAsync(Engine(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));

        await client.StartPodAsync("fixture-pod", TestContext.Current.CancellationToken);
        await client.StopPodAsync("fixture-pod", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_partial_failure_names_the_containers_errors()
    {
        // 409 with libpod's report: a list of errors, no message.
        using var client = await FakeEngineHandler.ConnectAsync(Engine(_ => FakeEngineHandler.Json(Fixture.Read("podman-pod-start-errors.json"), HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => client.RestartPodAsync("fixture-broken", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.StartsWith("restarting container f4d0a7f93b85", ex.Message, StringComparison.Ordinal);
        Assert.Contains("executable file `/nope` not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_that_arent_strings_still_say_something()
    {
        using var client = await FakeEngineHandler.ConnectAsync(Engine(_ => FakeEngineHandler.Json("""{"Errs":[{}],"Id":"x"}""", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => client.StartPodAsync("x", TestContext.Current.CancellationToken));

        Assert.Equal("Some of the pod's containers failed; the engine didn't say why.", ex.Message);
    }

    [Fact]
    public async Task Libpod_errors_carry_their_message()
    {
        using var client = await FakeEngineHandler.ConnectAsync(Engine(_ => FakeEngineHandler.Json(
            """{"cause":"no such pod","message":"no pod with name or ID nope found: no such pod","response":404}""", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => client.InspectPodAsync("nope", TestContext.Current.CancellationToken));

        Assert.True(ex.IsNotFound);
        Assert.Equal("no pod with name or ID nope found: no such pod", ex.Message);
    }

    [Fact]
    public void Remove_report_lists_the_removed_containers()
    {
        var report = Fixture.Load("podman-pod-rm.json", Json.PodRemoveReport);

        Assert.Equal(2, report.RemovedCtrs!.Count);
        Assert.All(report.RemovedCtrs.Values, error => Assert.Equal(System.Text.Json.JsonValueKind.Null, error?.ValueKind ?? System.Text.Json.JsonValueKind.Null));
    }
}
