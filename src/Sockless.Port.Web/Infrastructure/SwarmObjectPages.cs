using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>The list of secrets or configs (<c>/endpoints/{endpointId}/swarm/secrets</c> or <c>configs</c>).</summary>
public abstract class SwarmObjectsIndexModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public abstract SwarmObjectKind Kind { get; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<SwarmObject> Rows { get; private set; } = [];

    public int Total { get; private set; }

    public ILookup<string, SwarmObjectUse> Uses { get; private set; } = Array.Empty<SwarmObjectUse>().ToLookup(_ => "");

    public string Noun => SwarmObjects.Noun(Kind);

    public object Filters => new { endpointId = EndpointId, Q };

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page($"{SwarmObjects.Folder(Kind)}/Index", Filters);
        return Partial("_SwarmObjectTable", this);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? id, string? name)
    {
        if (string.IsNullOrEmpty(id))
            Flash.Error(TempData, $"Choose a {Noun} to remove.");
        else
            await TryEngineAsync(client => SwarmObjects.RemoveAsync(client, Kind, id, HttpContext.RequestAborted), $"Removed the {Noun} {name ?? id}.");
        return RedirectToPage(Filters);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        var objects = ReadAsync(c => SwarmObjects.ListAsync(c, Kind, cancellationToken), cancellationToken);
        var services = ReadAsync(c => c.ListServicesAsync(cancellationToken: cancellationToken), cancellationToken);
        await Task.WhenAll(objects, services);
        var all = objects.Result ?? [];
        Total = all.Count;
        Uses = SwarmObjects.Uses(Kind, services.Result ?? []);
        Rows = [.. all
            .Where(o => string.IsNullOrWhiteSpace(Q) || o.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) || o.Id.StartsWith(Q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Name, StringComparer.Ordinal)];
    }
}

/// <summary>Creates a secret or config from text or an uploaded file.</summary>
public abstract partial class SwarmObjectCreateModel(EngineConnections connections) : SwarmPageModel(connections)
{
    protected override EndpointPermission ReadPermission => EndpointPermission.ManageSwarm;

    public abstract SwarmObjectKind Kind { get; }

    [BindProperty]
    public string? Name { get; set; }

    /// <summary>The value as typed; ignored when a file is uploaded.</summary>
    [BindProperty]
    public string? Value { get; set; }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public string? Labels { get; set; }

    public string Noun => SwarmObjects.Noun(Kind);

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Manager is not { } client)
            return Page();

        if (SwarmObjects.NameProblem(Name) is { } problem)
            ModelState.AddModelError(nameof(Name), problem);

        Dictionary<string, string> labels = [];
        try
        {
            labels = ContainerSpecBuilder.ParseLabels(Labels);
        }
        catch (ContainerSpecBuilder.SpecFormatException ex)
        {
            ModelState.AddModelError(nameof(Labels), ex.Message);
        }

        byte[] value;
        if (Upload is { Length: > 0 } file)
        {
            if (file.Length > SwarmObjects.MaxSize(Kind))
            {
                ModelState.AddModelError(nameof(Upload), $"A {Noun} can hold at most {Format.BinaryBytes(SwarmObjects.MaxSize(Kind))}; this file is {Format.BinaryBytes(file.Length)}.");
                return Page();
            }
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            value = buffer.ToArray();
        }
        else
        {
            // Text from a textarea arrives with CRLF line ends; files on Linux expect LF.
            value = Encoding.UTF8.GetBytes(LineEnds().Replace(Value ?? "", "\n"));
        }
        if (value.Length == 0)
            ModelState.AddModelError(nameof(Value), $"Enter the {Noun}'s value, or choose a file.");
        else if (value.Length > SwarmObjects.MaxSize(Kind))
            ModelState.AddModelError(nameof(Value), $"A {Noun} can hold at most {Format.BinaryBytes(SwarmObjects.MaxSize(Kind))}.");

        if (!ModelState.IsValid)
            return Page();

        var name = Name!.Trim();
        try
        {
            var id = await SwarmObjects.CreateAsync(client, Kind, name, value, labels, cancellationToken);
            Flash.Success(TempData, Kind == SwarmObjectKind.Secret
                ? $"Created the secret {name}. Its value can't be read back: give it to a service to use it."
                : $"Created the config {name}.");
            return RedirectToPage($"{SwarmObjects.Folder(Kind)}/Detail", new { endpointId = EndpointId, objectId = id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
            return Page();
        }
    }

    [GeneratedRegex("\r\n?")]
    private static partial Regex LineEnds();
}

/// <summary>A secret's or config's metadata (and a config's value), the services using it, and removing it.</summary>
public abstract partial class SwarmObjectDetailModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public override string? AuditTarget => DisplayName;

    public const string RouteKey = "objectId";

    public abstract SwarmObjectKind Kind { get; }

    public string ObjectKey { get; private set; } = "";

    public SwarmObject? Object { get; private set; }

    public string? InspectJson { get; private set; }

    public IReadOnlyList<SwarmObjectUse> Users { get; private set; } = [];

    public string Noun => SwarmObjects.Noun(Kind);

    public string DisplayName => Object?.Name ?? ObjectKey;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectKey = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!ObjectKeyPattern().IsMatch(ObjectKey))
            return NotFound();
        if (Manager is not { } client)
            return null;
        try
        {
            (Object, InspectJson) = await SwarmObjects.InspectAsync(client, Kind, ObjectKey, cancellationToken);
            Users = [.. SwarmObjects.Uses(Kind, await client.ListServicesAsync(cancellationToken: cancellationToken))[Object.Id]
                .OrderBy(u => u.Service.Name, StringComparer.Ordinal)];
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return NotFound();
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
        return null;
    }

    public void OnGet()
    {
    }

    /// <summary>A config's value as a file.</summary>
    public IActionResult OnGetDownload() =>
        Kind == SwarmObjectKind.Config && Object?.Bytes is { } bytes
            ? File(bytes, "application/octet-stream", Object.Name)
            : NotFound();

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var ok = await TryEngineAsync(client => SwarmObjects.RemoveAsync(client, Kind, Object?.Id ?? ObjectKey, HttpContext.RequestAborted), $"Removed the {Noun} {DisplayName}.");
        return ok
            ? RedirectToPage($"{SwarmObjects.Folder(Kind)}/Index", new { endpointId = EndpointId })
            : RedirectToPage($"{SwarmObjects.Folder(Kind)}/Detail", new { endpointId = EndpointId, objectId = Object?.Id ?? ObjectKey });
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ObjectKeyPattern();
}
