using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Registries;

public sealed class EditModel(RegistryCredentials registries, EngineConnections connections) : PageModel
{
    public RegistryInfo Registry { get; private set; } = null!;

    [BindProperty]
    public RegistryForm Form { get; set; } = new();

    /// <summary>The engine a test login goes through: the endpoint last visited.</summary>
    public EngineConnection? TestEndpoint => EndpointSelection.Resolve(HttpContext, connections);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        if (await registries.GetAsync(id, cancellationToken) is not { } registry)
            return NotFound();
        Registry = registry;
        Form = new RegistryForm { Name = registry.Name, Registry = registry.Registry, Username = registry.Username };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        if (await registries.GetAsync(id, cancellationToken) is not { } registry)
            return NotFound();
        var errors = await registries.SaveAsync(id, Form.ToInput(), cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError($"Form.{error.Field}", error.Message);
            Registry = registry;
            Form.Password = null;
            return Page();
        }
        Flash.Success(TempData, $"Saved {Form.Name.Trim()}.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken cancellationToken)
    {
        var name = await registries.RemoveAsync(id, cancellationToken);
        if (name is null)
            return NotFound();
        Flash.Success(TempData, $"Removed the credentials {name}. Pulls from that registry are anonymous again.");
        return RedirectToPage("/Registries/Index");
    }

    /// <summary>Logs in to the registry through the engine (<c>POST /auth</c>), without storing anything there.</summary>
    public async Task<IActionResult> OnPostTestAsync(int id, CancellationToken cancellationToken)
    {
        if (await registries.GetAsync(id, cancellationToken) is not { } registry)
            return NotFound();
        if (await registries.GetAuthAsync(id, cancellationToken) is not { } auth)
        {
            Flash.Error(TempData, "The stored password can't be read any more. Enter it again and save.");
            return RedirectToPage(new { id });
        }
        if (TestEndpoint is not { Client: { } client } endpoint)
        {
            Flash.Error(TempData, "No engine is online to test the login with.");
            return RedirectToPage(new { id });
        }
        try
        {
            var result = await client.CheckAuthAsync(auth, cancellationToken);
            Flash.Success(TempData, $"{endpoint.Name} logged in to {registry.Registry} as {registry.Username}{(string.IsNullOrEmpty(result.Status) ? "." : $": {result.Status}")}");
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Flash.Error(TempData, $"The login to {registry.Registry} failed. {EngineFailures.Describe(ex, endpoint)}");
        }
        return RedirectToPage(new { id });
    }
}
