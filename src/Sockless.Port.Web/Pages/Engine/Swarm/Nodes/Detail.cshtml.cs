using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Nodes;

/// <summary>A node's tasks and details, and the handlers that change it.</summary>
public sealed class DetailModel(EngineConnections connections) : NodePageModel(connections)
{
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Swarm/Nodes/Detail", "Overview"),
        ("inspect", "/Engine/Swarm/Nodes/Inspect", "Inspect"),
    ];

    public static readonly IReadOnlyList<(string Value, string Label, string Hint)> Availabilities =
    [
        ("active", "Active", "Takes new tasks."),
        ("pause", "Pause", "Keeps its tasks, takes no new ones."),
        ("drain", "Drain", "Its tasks move to other nodes."),
    ];

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the tasks: the forms around them aren't swapped, so typing isn't lost.</summary>
    public IActionResult OnGetOverview() => Partial("_NodeTasks", this);

    public async Task<IActionResult> OnPostAvailabilityAsync(string? availability)
    {
        if (Availabilities.All(a => a.Value != availability))
        {
            Flash.Error(TempData, "Choose active, pause or drain.");
            return Back();
        }
        await UpdateAsync(spec => spec with { Availability = availability }, availability switch
        {
            "drain" => $"Draining {DisplayName}: its tasks move to other nodes.",
            "pause" => $"Paused {DisplayName}: it keeps its tasks and takes no new ones.",
            _ => $"{DisplayName} takes tasks again. Running tasks don't move back by themselves; restart a service to spread it again.",
        });
        return Back();
    }

    /// <param name="role"><c>manager</c> to promote, <c>worker</c> to demote.</param>
    public async Task<IActionResult> OnPostRoleAsync(string? role)
    {
        if (role is not ("manager" or "worker"))
        {
            Flash.Error(TempData, "Choose manager or worker.");
            return Back();
        }
        await UpdateAsync(spec => spec with { Role = role }, role == "manager"
            ? $"Promoted {DisplayName} to manager. It takes part in the cluster's decisions now."
            : $"Demoted {DisplayName} to worker.");
        return Back();
    }

    public async Task<IActionResult> OnPostLabelsAsync(string? labels)
    {
        Dictionary<string, string> parsed;
        try
        {
            parsed = ContainerSpecBuilder.ParseLabels(labels);
        }
        catch (ContainerSpecBuilder.SpecFormatException ex)
        {
            Flash.Error(TempData, $"Labels: {ex.Message}");
            return Back();
        }
        await UpdateAsync(spec => spec with { Labels = parsed }, $"Saved the labels of {DisplayName}. Constraints and spread use them for new tasks.");
        return Back();
    }

    public async Task<IActionResult> OnPostRemoveAsync(bool force)
    {
        if (IsThisNode)
        {
            Flash.Error(TempData, $"{DisplayName} is the node {Connection.Name} talks to, so it can't be removed from here. Run docker swarm leave on it.");
            return Back();
        }
        var ok = await TryEngineAsync(client => client.RemoveNodeAsync(NodeId, force, HttpContext.RequestAborted),
            $"Removed {DisplayName} from the swarm.{(force ? " If it's still running, run docker swarm leave --force on it." : "")}");
        return ok ? RedirectToPage("/Engine/Swarm/Nodes/Index", new { endpointId = EndpointId }) : Back();
    }

    private async Task UpdateAsync(Func<NodeSpec, NodeSpec> change, string success)
    {
        if (Node is null)
        {
            Flash.Error(TempData, "The node couldn't be read, so nothing was changed.");
            return;
        }
        var spec = Node.Spec ?? new NodeSpec();
        await TryEngineAsync(client => client.UpdateNodeAsync(Node.Id, Node.Version, change(spec), HttpContext.RequestAborted), success);
    }

    private RedirectToPageResult Back() => RedirectToPage("/Engine/Swarm/Nodes/Detail", new { endpointId = EndpointId, nodeId = NodeId });
}
