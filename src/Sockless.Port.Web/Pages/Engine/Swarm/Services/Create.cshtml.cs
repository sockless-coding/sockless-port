using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

public sealed class CreateModel(EngineConnections connections, SwarmServices services) : SwarmPageModel(connections), IServiceFormPage
{
    protected override EndpointPermission ReadPermission => EndpointPermission.ManageSwarm;

    [BindProperty]
    public ServiceForm Input { get; set; } = new();

    public ServiceReferences References { get; private set; } = ServiceReferences.Empty;

    public bool Editing => false;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        References = await ReadAsync(c => ServiceForms.LoadReferencesAsync(c, cancellationToken), cancellationToken) ?? ServiceReferences.Empty;

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Manager is not { } client)
            return Page();
        References = await ReadAsync(c => ServiceForms.LoadReferencesAsync(c, cancellationToken), cancellationToken) ?? ServiceReferences.Empty;

        var (spec, errors) = ServiceSpecBuilder.Build(Input, References);
        ServiceForms.AddErrors(ModelState, errors);
        if (spec is null)
            return Page();
        try
        {
            var created = await services.CreateAsync(client, spec, cancellationToken);
            var warnings = created.Warnings.Count == 0 ? "" : " " + string.Join(" ", created.Warnings);
            Flash.Success(TempData, $"Created the service {Input.Name?.Trim()}. Its tasks are being placed.{warnings}");
            return RedirectToPage("/Engine/Swarm/Services/Detail", new { endpointId = EndpointId, serviceId = created.Id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            return Page();
        }
    }
}
