using Sockless.Port.Docker.Models;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

/// <summary>The parts of the lists across endpoints that don't need an engine.</summary>
public sealed class EndpointListTests
{
    [Fact]
    public void Posted_ids_are_split_by_endpoint_in_order()
    {
        var split = EndpointReads.ByEndpoint(["3:abc", "1:def", "3:ghi", "3:abc", "x:1", ":1", "2:", "4"]);

        Assert.Equal(2, split.Count);
        Assert.Equal(3, split[0].EndpointId);
        Assert.Equal(["abc", "ghi"], split[0].Ids);
        Assert.Equal(1, split[1].EndpointId);
        Assert.Equal(["def"], split[1].Ids);
    }

    [Fact]
    public void A_swarms_stack_is_listed_once_preferring_the_managed_one()
    {
        var managed = new StackDefinition { Name = "web", Kind = StackKind.Swarm, EndpointId = 2 };
        IEnumerable<StackRow> rows =
        [
            Stack(1, "web", null, "swarm-a"),
            Stack(2, "web", managed, "swarm-a"),
            Stack(3, "web", null, "swarm-b"),       // another swarm: its own stack
            Stack(1, "db", null, "swarm-a"),
            Stack(4, "web", null, null, StackKind.Compose),
            Stack(5, "web", null, null, StackKind.Compose),
        ];

        var kept = StackListing.OnePerSwarm(rows).Select(r => (r.EndpointId, r.Name)).ToList();

        Assert.Equal(5, kept.Count);
        Assert.Contains((2, "web"), kept);
        Assert.DoesNotContain((1, "web"), kept);
        Assert.Contains((3, "web"), kept);
        Assert.Contains((1, "db"), kept);
        Assert.Contains((4, "web"), kept);
        Assert.Contains((5, "web"), kept);
    }

    [Fact]
    public void An_endpoints_containers_group_by_stack_with_loose_ones_last()
    {
        IEnumerable<ContainerRow> rows = [Container("web-1", "web"), Container("solo", null), Container("api-1", "Api"), Container("web-2", "web")];

        var groups = ContainerGroup.Of(rows);

        Assert.Equal(["Api", "web", null], groups.Select(g => g.Stack));
        Assert.Equal(["web-1", "web-2"], groups[1].Rows.Select(r => r.Name));
        Assert.Equal(":web", groups[1].Key);
        Assert.Null(groups[1].Endpoint);
    }

    private static StackRow Stack(int endpoint, string name, StackDefinition? definition, string? cluster, StackKind kind = StackKind.Swarm) =>
        new(endpoint, name, definition, kind, ComposeProject.Empty(name), SwarmStack.Empty(name), null) { SwarmClusterId = cluster };

    private static ContainerRow Container(string name, string? stack) =>
        new(1, new ContainerSummary { Id = name, Names = ["/" + name], State = "running" }, new StatusBadge("Running", "success"), null, stack, null, []);
}
