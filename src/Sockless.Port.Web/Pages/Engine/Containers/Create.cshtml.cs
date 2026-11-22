using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Registries;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class CreateModel(EngineConnections connections, RegistryCredentials registries) : EndpointPageModel(connections)
{
    /// <summary>A form for changing things: viewers don't get it.</summary>
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    [BindProperty]
    public ContainerSpec Input { get; set; } = new();

    /// <summary><c>missing</c> (pull only if the image isn't on the engine) or <c>always</c>.</summary>
    [BindProperty]
    public string Pull { get; set; } = "missing";

    [BindProperty]
    public bool Start { get; set; } = true;

    /// <summary>Set by images.js after it pulled the image with live progress, so the post doesn't pull again.</summary>
    [BindProperty]
    public bool Pulled { get; set; }

    /// <summary>The container being duplicated, if any.</summary>
    public string? From { get; private set; }

    public IReadOnlyList<string> Networks { get; private set; } = [];

    public string? Error { get; private set; }

    /// <param name="from">A container to duplicate.</param>
    /// <param name="image">An image to start from (the image page's "Create container").</param>
    public async Task<IActionResult> OnGetAsync(string? from, string? image, CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return Page();

        if (!string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(from))
            Input.Image = image.Trim();

        if (!string.IsNullOrWhiteSpace(from))
        {
            try
            {
                var (container, _) = await client.InspectContainerAsync(from, cancellationToken);
                Input = ContainerSpecMapper.FromInspect(container);
                From = container.DisplayName;
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
                return NotFound();
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Error = EngineFailures.Describe(ex, Connection);
            }
        }
        await LoadNetworksAsync(client, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return Page();

        var (body, errors) = ContainerSpecBuilder.Build(Input);
        foreach (var error in errors)
            ModelState.AddModelError($"{nameof(Input)}.{error.Field}", error.Message);
        if (body is null)
        {
            await LoadNetworksAsync(client, cancellationToken);
            return Page();
        }

        var image = Input.Image.Trim();
        var name = string.IsNullOrWhiteSpace(Input.Name) ? null : Input.Name.Trim();
        try
        {
            if (!Pulled && (Pull == "always" || !await client.ImageExistsAsync(image, cancellationToken)))
            {
                var auth = await registries.AuthHeaderForAsync(image, cancellationToken);
                await foreach (var _ in client.PullImageAsync(image, auth, cancellationToken)) { }
            }

            var created = await client.CreateContainerAsync(name, body, cancellationToken);
            if (Start)
                await client.StartContainerAsync(created.Id, cancellationToken);

            var display = name ?? created.Id[..12];
            var warnings = created.Warnings is { Count: > 0 } w ? $" The engine warned: {string.Join(" ", w)}" : "";
            Flash.Success(TempData, $"{(Start ? "Created and started" : "Created")} {display}.{warnings}");
            return RedirectToPage("/Engine/Containers/Detail", new { endpointId = EndpointId, containerId = created.Id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            await LoadNetworksAsync(client, cancellationToken);
            return Page();
        }
    }

    private async Task LoadNetworksAsync(DockerClient client, CancellationToken cancellationToken)
    {
        try
        {
            Networks = [.. (await client.ListNetworksAsync(cancellationToken)).Select(n => n.Name).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Networks = [];
        }
    }
}
