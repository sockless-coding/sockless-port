using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Networks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Networks;

public sealed class CreateModel(EngineConnections connections) : EndpointPageModel(connections)
{
    [BindProperty]
    public NetworkCreateSpec Input { get; set; } = new();

    /// <summary>Drivers a network can be created with on this engine.</summary>
    public IReadOnlyList<string> Drivers { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadDriversAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return Page();

        await LoadDriversAsync(cancellationToken);
        var (request, errors) = NetworkCreateSpecBuilder.Build(Input, Drivers);
        foreach (var error in errors)
            ModelState.AddModelError($"{nameof(Input)}.{error.Field}", error.Message);
        if (request is null)
            return Page();

        try
        {
            // Docker allows two networks with one name (and then can't address either by name); refuse that here.
            if ((await client.ListNetworksAsync(cancellationToken)).Any(n => n.Name == request.Name))
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Name)}", $"A network named {request.Name} already exists.");
                return Page();
            }
            var created = await client.CreateNetworkAsync(request, cancellationToken);
            var warning = string.IsNullOrWhiteSpace(created.Warning) ? "" : $" The engine warned: {created.Warning}";
            Flash.Success(TempData, $"Created the network {request.Name}.{warning}");
            return RedirectToPage("/Engine/Networks/Detail", new { endpointId = EndpointId, networkId = created.Id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            return Page();
        }
    }

    private async Task LoadDriversAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        IReadOnlyList<string>? engineDrivers = null;
        try
        {
            engineDrivers = (await client.GetInfoAsync(cancellationToken)).Plugins?.Network;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
        }
        Drivers = NetworkCreateSpecBuilder.Drivers(engineDrivers, Connection.Engine?.Capabilities ?? EngineCapabilities.None);
    }
}
