using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Volumes;

public sealed class CreateModel(EngineConnections connections) : EndpointPageModel(connections)
{
    [BindProperty]
    public VolumeCreateSpec Input { get; set; } = new();

    /// <summary>Volume drivers the engine has, for suggestions.</summary>
    public IReadOnlyList<string> Drivers { get; private set; } = ["local"];

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadDriversAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return Page();

        var (request, errors) = VolumeCreateSpecBuilder.Build(Input);
        foreach (var error in errors)
            ModelState.AddModelError($"{nameof(Input)}.{error.Field}", error.Message);
        if (request is null)
        {
            await LoadDriversAsync(cancellationToken);
            return Page();
        }

        try
        {
            // The engine returns an existing volume of the same name instead of failing; say so.
            if (request.Name is { } name && (await client.ListVolumesAsync(cancellationToken)).Any(v => v.Name == name))
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Name)}", $"A volume named {name} already exists.");
                await LoadDriversAsync(cancellationToken);
                return Page();
            }
            var created = await client.CreateVolumeAsync(request, cancellationToken);
            Flash.Success(TempData, $"Created the volume {VolumePageModel.ShortName(created.Name)}.");
            return RedirectToPage("/Engine/Volumes/Detail", new { endpointId = EndpointId, volumeName = created.Name });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            await LoadDriversAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadDriversAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            if ((await client.GetInfoAsync(cancellationToken)).Plugins?.Volume is { Count: > 0 } drivers)
                Drivers = drivers;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
        }
    }
}
