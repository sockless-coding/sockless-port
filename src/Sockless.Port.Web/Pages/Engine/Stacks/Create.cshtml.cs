using Microsoft.AspNetCore.Mvc;
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
    [BindProperty]
    public StackInput Input { get; set; } = new();

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
            return Page();

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
        Validation = await StackForms.ValidateAsync(validator, store, Connection, StackForms.NameForCheck(Adopting ? Adopt : Input.Name), Input.Content, cancellationToken);
        return Request.Headers.ContainsKey("HX-Request") ? Partial("_StackValidation", Validation) : Page();
    }

    /// <param name="deploy">Deploy right away, rather than only saving.</param>
    public async Task<IActionResult> OnPostAsync(bool deploy, CancellationToken cancellationToken)
    {
        var name = (Adopting ? Adopt : Input.Name)?.Trim() ?? "";
        Input.Name = name;
        if (StackNames.Problem(name) is { } problem)
            ModelState.AddModelError("Input.Name", problem);
        else if (await store.GetAsync(EndpointId, name, cancellationToken) is not null)
            ModelState.AddModelError("Input.Name", $"There's already a stack named {name}. Open it to edit its files.");
        else if (!Adopting && Client is { } client && await RunsOutsideAsync(client, name, cancellationToken))
            ModelState.AddModelError("Input.Name", $"A Compose project named {name} already runs on {Connection.Name}. Adopt it from the stack list, or choose another name.");

        var content = Input.Content;
        if (string.IsNullOrWhiteSpace(content.Compose))
            ModelState.AddModelError("Input.Compose", "Write the compose file: at least one service with an image.");

        if (ModelState.IsValid)
        {
            Validation = await StackForms.ValidateAsync(validator, store, Connection, name, content, cancellationToken);
            if (Validation.Check is { Valid: false })
                ModelState.AddModelError("Input.Compose", "Compose rejected the file. Fix the problems listed above it.");
            else if (Validation.Check is null && deploy)
                ModelState.AddModelError("Input.Compose", "The files can't be checked or deployed without Compose. Save them for now.");
        }
        if (!ModelState.IsValid)
            return Page();

        try
        {
            await store.CreateAsync(EndpointId, name, content, adopted: Adopting, cancellationToken);
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

    private async Task<bool> RunsOutsideAsync(Docker.DockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            return (await StackPageModel.LoadProjectAsync(client, name, cancellationToken)).Containers.Count > 0;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return false;
        }
    }
}
