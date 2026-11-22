using System.Net;
using System.Text.Json;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Pods;

namespace Sockless.Port.Core.Tests;

public sealed class PodMembershipTests
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    private static T Fixture<T>(string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), Read)!;

    [Fact]
    public void Containers_are_matched_to_their_pods_from_the_pod_list()
    {
        // Captured together: the pods and Docker's container list on the same engine.
        var membership = PodMembership.From(Fixture<List<PodSummary>>("podman-pods.json"));
        var containers = Fixture<List<ContainerSummary>>("podman-pod-containers.json");

        var web = membership.Of(Assert.Single(containers, c => c.DisplayName == "fixture-pod-web").Id);
        Assert.Equal(("fixture-pod", false), (web?.Pod.Name, web?.IsInfra));

        var infra = Assert.Single(containers, c => membership.Of(c.Id) is { IsInfra: true, Pod.Name: "fixture-pod" });
        Assert.EndsWith("-infra", infra.DisplayName, StringComparison.Ordinal);

        Assert.Null(membership.Of(Assert.Single(containers, c => c.DisplayName == "fixture-solo").Id));
        Assert.All(containers.Where(c => c.DisplayName != "fixture-solo"), c => Assert.NotNull(membership.Of(c.Id)));
    }

    [Fact]
    public void Only_start_is_allowed_on_the_pod_sockless_port_runs_in()
    {
        Assert.False(PodActions.EndsSelf(PodAction.Start, podIsSelf: true));
        Assert.True(PodActions.EndsSelf(PodAction.Stop, podIsSelf: true));
        Assert.True(PodActions.EndsSelf(PodAction.Restart, podIsSelf: true));
        Assert.True(PodActions.EndsSelf(PodAction.Remove, podIsSelf: true));
        Assert.False(PodActions.EndsSelf(PodAction.Remove, podIsSelf: false));
    }
}

public sealed class EngineHintsTests
{
    [Fact]
    public void A_container_without_a_cgroup_gets_the_specific_hint_only()
    {
        // Podman's answer to stats of a container in a nested engine.
        var hints = EngineHints.For("failed to obtain Container web stats: cannot run top on container 7e0f as it did not create a cgroup: this container does not have a cgroup");

        var hint = Assert.Single(hints);
        Assert.Contains("can't measure, limit or pause it", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_cgroup_failures_get_the_general_hint()
    {
        var hint = Assert.Single(EngineHints.For("pausing container c506: cannot pause without using Cgroups"));
        Assert.Contains("resource limits, stats and pausing", hint, StringComparison.Ordinal);
    }

    [Theory]
    // Rootless Podman and rootless Docker word it differently.
    [InlineData("rootlessport cannot expose privileged port 80, you can add 'net.ipv4.ip_unprivileged_port_start=80' to /etc/sysctl.conf (currently 1024)")]
    [InlineData("driver failed programming external connectivity: error while calling RootlessKit PortManager.AddPort(): cannot expose privileged port 80")]
    public void Privileged_ports_on_rootless_engines_are_explained(string message) =>
        Assert.Contains(EngineHints.For(message), h => h.Contains("below 1024", StringComparison.Ordinal));

    [Fact]
    public void Engine_call_failures_carry_the_hint_after_the_engines_message()
    {
        var error = new DockerApiException(HttpStatusCode.Conflict, "this container does not have a cgroup", "/containers/x/stats");

        var message = EngineErrors.ForCall(error, new UnixSocketAddress("/run/podman/podman.sock"));

        Assert.StartsWith("This container does not have a cgroup. The container has no cgroup of its own", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_failures_are_left_as_the_engine_said()
    {
        var error = new DockerApiException(HttpStatusCode.NotFound, "no such container", "/containers/x/json");

        Assert.Equal("No such container.", EngineErrors.ForCall(error, new UnixSocketAddress("/run/podman/podman.sock")));
    }
}
