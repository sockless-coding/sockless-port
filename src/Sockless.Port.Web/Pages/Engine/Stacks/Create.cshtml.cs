using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>
/// A new stack from a compose file, or (with <c>?adopt=name</c>) a project deployed outside
/// Sockless Port, prefilled from its compose file or its containers.
/// </summary>
public sealed class CreateModel(EngineConnections connections, StackStore store, StackJobs jobs, StackValidator validator) : EndpointPageModel(connections)
{
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    /// <summary>A swarm stack deploys services across the swarm.</summary>
    protected override EndpointPermission WritePermission =>
        ChosenKind == StackKind.Swarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate;

    [BindProperty]
    public StackInput Input { get; set; } = new();

    /// <summary>How the new stack deploys. Swarm is offered (and preselected) on swarm managers.</summary>
    [BindProperty]
    public StackKind Kind { get; set; } = StackKind.Compose;

    /// <summary>The endpoint can deploy swarm stacks.</summary>
    public bool OffersSwarm => Connection.Engine?.Has(Docker.Engine.EngineCapabilities.SwarmManager) == true;

    /// <summary>The project being adopted.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Adopt { get; set; }

    /// <summary>Where the adopted file came from; null when it was rebuilt from the containers.</summary>
    public string? AdoptSource { get; private set; }

    public bool Adopting => !string.IsNullOrEmpty(Adopt);

    public StackValidationView? Validation { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Adopting)
        {
            Kind = OffersSwarm ? StackKind.Swarm : StackKind.Compose;
            return Page();
        }

        if (StackNames.Problem(Adopt) is not null)
            return NotFound();
        if (await store.GetAsync(EndpointId, Adopt!, cancellationToken) is not null)
            return RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = Adopt });
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so the project can't be read. Try again once it's back.");
            return RedirectToPage("/Engine/Stacks/Index", new { endpointId = EndpointId });
        }

        try
        {
            var project = await StackPageModel.LoadProjectAsync(client, Adopt!, cancellationToken);
            if (project.Containers.Count == 0)
            {
                Flash.Error(TempData, $"There's no Compose project named {Adopt} on {Connection.Name} any more.");
                return RedirectToPage("/Engine/Stacks/Index", new { endpointId = EndpointId });
            }
            if (project.IsSelf)
            {
                Flash.Error(TempData, $"Sockless Port itself runs in {Adopt}, so it can't manage it.");
                return RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = Adopt });
            }
            var adopted = await StackAdopter.AdoptAsync(client, project, cancellationToken);
            AdoptSource = adopted.Source;
            Input = new StackInput { Name = Adopt, Compose = adopted.Content.Compose, Env = adopted.Content.Env };
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = $"Couldn't read the project: {EngineFailures.Describe(ex, Connection)}";
        }
        return Page();
    }

    /// <summary>Checks the files with docker compose config: the result alone for htmx, the whole form otherwise.</summary>
    public async Task<IActionResult> OnPostValidateAsync(CancellationToken cancellationToken)
    {
        Validation = await StackForms.ValidateAsync(validator, store, Connection, StackForms.NameForCheck(Adopting ? Adopt : Input.Name), Input.Content, ChosenKind, cancellationToken);
        return Request.Headers.ContainsKey("HX-Request") ? Partial("_StackValidation", Validation) : Page();
    }

    /// <param name="deploy">Deploy right away, rather than only saving.</param>
    public async Task<IActionResult> OnPostAsync(bool deploy, CancellationToken cancellationToken)
    {
        var name = (Adopting ? Adopt : Input.Name)?.Trim() ?? "";
        Input.Name = name;
        var kind = ChosenKind;
        if (StackNames.Problem(name) is { } problem)
            ModelState.AddModelError("Input.Name", problem);
        else if (await store.GetAsync(EndpointId, name, cancellationToken) is not null)
            ModelState.AddModelError("Input.Name", $"There's already a stack named {name}. Open it to edit its files.");
        else if (!Adopting && Client is { } client && await RunsOutsideAsync(client, name, cancellationToken) is { } outside)
            ModelState.AddModelError("Input.Name", outside);

        var content = Input.Content;
        if (string.IsNullOrWhiteSpace(content.Compose))
            ModelState.AddModelError("Input.Compose", "Write the compose file: at least one service with an image.");

        if (ModelState.IsValid)
        {
            Validation = await StackForms.ValidateAsync(validator, store, Connection, name, content, kind, cancellationToken);
            if (Validation.Check is { Valid: false })
                ModelState.AddModelError("Input.Compose", "Compose rejected the file. Fix the problems listed above it.");
            else if (Validation.Check is null && deploy)
                ModelState.AddModelError("Input.Compose", "The files can't be checked or deployed without Compose. Save them for now.");
        }
        if (!ModelState.IsValid)
            return Page();

        try
        {
            await store.CreateAsync(EndpointId, name, content, adopted: Adopting, kind, cancellationToken);
        }
        catch (StackRejectedException ex)
        {
            ModelState.AddModelError("Input.Name", ex.Message);
            return Page();
        }

        if (deploy)
        {
            try
            {
                await jobs.StartAsync(Connection, name, StackOperation.Deploy);
            }
            catch (StackJobRejectedException ex)
            {
                Flash.Error(TempData, $"Saved {name}, but it wasn't deployed: {ex.Message}");
            }
        }
        else
        {
            Flash.Success(TempData, Adopting
                ? $"Adopted {name}. Its containers keep running; deploy it from here when you change it."
                : $"Saved {name}. It isn't deployed yet.");
        }
        return RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = name });
    }

    /// <summary>Adopted stacks are Compose projects; swarm stacks can't be adopted yet.</summary>
    private StackKind ChosenKind => !Adopting && OffersSwarm && Kind == StackKind.Swarm ? StackKind.Swarm : StackKind.Compose;

    /// <summary>Why the name is taken on the engine by something Sockless Port doesn't manage; null when it's free.</summary>
    private async Task<string?> RunsOutsideAsync(Docker.DockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            if ((await StackPageModel.LoadProjectAsync(client, name, cancellationToken)).Containers.Count > 0)
                return $"A Compose project named {name} already runs on {Connection.Name}. Adopt it from the stack list, or choose another name.";
            if (OffersSwarm && (await StackPageModel.LoadSwarmAsync(client, name, cancellationToken)).Services.Count > 0)
                return $"A swarm stack named {name} already runs on this swarm. Choose another name.";
            return null;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return null;
        }
    }
}
