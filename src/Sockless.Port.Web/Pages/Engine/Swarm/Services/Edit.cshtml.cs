using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

/// <summary>
/// Edits a service. The form is applied onto the service's own spec, so settings it doesn't show
/// (healthchecks, restart policy, log driver, …) stay as they are.
/// </summary>
public sealed class EditModel(EngineConnections connections, SwarmServices services) : ServicePageModel(connections), IServiceFormPage
{
    protected override EndpointPermission ReadPermission => EndpointPermission.ManageSwarm;

    [BindProperty]
    public ServiceForm Input { get; set; } = new();

    public ServiceReferences References { get; private set; } = ServiceReferences.Empty;

    public bool Editing => true;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        References = await ReadAsync(c => ServiceForms.LoadReferencesAsync(c, cancellationToken), cancellationToken) ?? ServiceReferences.Empty;
        if (Service?.Spec is { } spec)
            Input = ServiceSpecBuilder.FromSpec(spec, References);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Manager is not { } client || Service is null || SpecJson is not { } current)
            return Page();
        References = await ReadAsync(c => ServiceForms.LoadReferencesAsync(c, cancellationToken), cancellationToken) ?? ServiceReferences.Empty;

        var (spec, errors) = ServiceSpecBuilder.Build(Input, References, current);
        ServiceForms.AddErrors(ModelState, errors);
        if (spec is null)
            return Page();
        try
        {
            var updated = await services.UpdateAsync(client, Service, spec, cancellationToken);
            var warnings = updated.Warnings.Count == 0 ? "" : " " + string.Join(" ", updated.Warnings);
            Flash.Success(TempData, $"Saved {DisplayName}. Its tasks are replaced as its update config says.{warnings}");
            return RedirectToPage("/Engine/Swarm/Services/Detail", new { endpointId = EndpointId, serviceId = ServiceId });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            return Page();
        }
    }
}
