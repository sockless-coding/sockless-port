using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a stack's pages (<c>/endpoints/{endpointId}/stacks/{stackName}/…</c>). A stack is
/// either managed (a definition with files) or external (a Compose project on the engine that
/// Sockless Port didn't deploy). Unknown names are 404, unless a job on that name is still shown
/// (after a remove, its page shows how the removal went).
/// </summary>
public abstract class StackPageModel(EngineConnections connections, StackStore store, StackJobs jobs) : EndpointPageModel(connections)
{
    public const string RouteKey = "stackName";

    public string StackName { get; private set; } = "";

    /// <summary>Set when Sockless Port manages the stack.</summary>
    public StackDefinition? Definition { get; private set; }

    /// <summary>The stack's containers on the engine; empty while offline or when it's down.</summary>
    public ComposeProject Project { get; private set; } = ComposeProject.Empty("");

    /// <summary>The running job, or the last one that's still kept.</summary>
    public StackJob? Job { get; private set; }

    /// <summary>Set when the engine is online but listing failed.</summary>
    public string? Error { get; private set; }

    public bool IsManaged => Definition is not null;

    /// <summary>Neither managed nor on the engine: only a finished job's outcome is left to show.</summary>
    public bool IsGone => Definition is null && Project.Containers.Count == 0;

    public bool IsBusy => Job?.IsRunning == true;

    /// <summary>Where the stack's files are, and where Compose runs.</summary>
    public string StackFolder => store.Files.Directory(EndpointId, StackName);

    protected StackStore Store => store;

    protected StackJobs Jobs => jobs;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        StackName = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (StackNames.Problem(StackName) is not null)
            return NotFound();

        Definition = await store.GetAsync(EndpointId, StackName, cancellationToken);
        Project = ComposeProject.Empty(StackName);
        Job = jobs.Latest(EndpointId, StackName);

        if (Client is { } client)
        {
            try
            {
                Project = await LoadProjectAsync(client, StackName, cancellationToken);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Error = EngineFailures.Describe(ex, Connection);
            }
        }

        // Offline, an external stack can't be told apart from a typo; a managed one still opens.
        if (Definition is null && Project.Containers.Count == 0 && Job is null && Error is null)
            return NotFound();
        return null;
    }

    /// <summary>The project's containers, found by the Compose label.</summary>
    public static async Task<ComposeProject> LoadProjectAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        var containers = await client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{ContainerLabels.ComposeProject}={name}"] }, all: true, cancellationToken);
        return StackInventory.Projects(containers).GetValueOrDefault(name) ?? ComposeProject.Empty(name);
    }

    /// <summary>Starts a job, flashing why when it can't. Returns whether it started.</summary>
    protected async Task<bool> TryStartAsync(StackOperation operation, StackJobOptions? options = null)
    {
        try
        {
            Job = await jobs.StartAsync(Connection, StackName, operation, options);
            return true;
        }
        catch (StackJobRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
            return false;
        }
    }
}
