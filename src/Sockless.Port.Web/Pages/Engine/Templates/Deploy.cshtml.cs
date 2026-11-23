using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Templates;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Templates;

/// <summary>The form's fields, as posted.</summary>
public sealed class TemplateForm
{
    public string? Name { get; set; }

    public Dictionary<string, string?> Variables { get; set; } = new(StringComparer.Ordinal);

    public List<PortField> Ports { get; set; } = [];

    public List<VolumeField> Volumes { get; set; } = [];

    public sealed class PortField
    {
        public bool Publish { get; set; }

        public string? Host { get; set; }
    }

    public sealed class VolumeField
    {
        public bool Include { get; set; }

        public string? Source { get; set; }
    }

    public static TemplateForm From(string name, TemplateChoices choices) => new()
    {
        Name = name,
        Variables = choices.Variables.ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.Ordinal),
        Ports = choices.Ports.Select(p => new PortField { Publish = p.Publish, Host = p.Host }).ToList(),
        Volumes = choices.Volumes.Select(v => new VolumeField { Include = v.Include, Source = v.Source }).ToList(),
    };

    public TemplateChoices Choices(AppTemplate template) => new(
        template.Variables.ToDictionary(v => v.Name, v => Variables.TryGetValue(v.Name, out var value) ? value ?? "" : v.Optional ? "" : v.Default, StringComparer.Ordinal),
        template.Ports.Select((_, i) => i < Ports.Count ? new PortChoice(Ports[i].Publish, Ports[i].Host?.Trim()) : new PortChoice(false, null)).ToList(),
        template.Volumes.Select((_, i) => i < Volumes.Count ? new VolumeChoice(Volumes[i].Include, Volumes[i].Source?.Trim()) : new VolumeChoice(false, null)).ToList());
}

/// <summary>
/// One template: what it is, the choices it asks for, and the compose file they make. Saving
/// creates the stack (not deployed) and opens its review, where it's deployed like any other.
/// </summary>
public sealed class DeployModel(EngineConnections connections, TemplateCatalog catalog, StackStore store) : EndpointPageModel(connections)
{
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    protected override EndpointPermission WritePermission =>
        Item?.Template.Kind == TemplateKind.Swarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate;

    [BindProperty(SupportsGet = true)]
    public int SourceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Key { get; set; } = "";

    [BindProperty]
    public TemplateForm Form { get; set; } = new();

    public SourcedTemplate? Item { get; private set; }

    public AppTemplate Template => Item!.Template;

    /// <summary>The files the current choices make; null when they can't be made (see <see cref="FileError"/>).</summary>
    public StackContent? Preview { get; private set; }

    /// <summary>Why a stack template's file couldn't be downloaded.</summary>
    public string? FileError { get; private set; }

    /// <summary>What the compose file asks of the host; null when it couldn't be read.</summary>
    public IReadOnlyList<TemplateRisk>? Risks { get; private set; }

    public IReadOnlyList<NotePart> Note { get; private set; } = [];

    /// <summary>Problems with the choices, by field name (<see cref="TemplateChoiceError.Field"/>).</summary>
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public StackKind StackKind => Template.Kind == TemplateKind.Swarm ? StackKind.Swarm : StackKind.Compose;

    public override string? AuditTarget => Item is null ? null : $"{Form.Name} ({Item.Template.Title}, {Item.Source.Name})";

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        Item = await catalog.FindAsync(SourceId, Key, cancellationToken);
        if (Item is null || !TemplateDisplay.Offered(Item.Template, Connection, Access, EndpointId))
            return NotFound();
        Note = TemplateNote.Parse(Item.Template.Note);
        // Also for posts refused for the user's role.
        AuditTrail.Action(HttpContext, "Templates: create stack");
        return null;
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Form = TemplateForm.From(await DefaultNameAsync(cancellationToken), TemplateChoices.Defaults(Template));
        await PreviewAsync(Form.Choices(Template), cancellationToken);
    }

    /// <summary>Shows the files the posted choices make, without saving.</summary>
    [NoAudit]
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        var choices = Form.Choices(Template);
        Check(choices);
        await PreviewAsync(choices, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Template.Unavailable is { } unavailable)
        {
            Flash.Error(TempData, unavailable);
            return RedirectToPage(new { endpointId = EndpointId, sourceId = SourceId, key = Key });
        }

        var name = Form.Name?.Trim() ?? "";
        Form.Name = name;
        var choices = Form.Choices(Template);
        if (StackNames.Problem(name) is { } problem)
            ModelState.AddModelError("Form.Name", problem);
        else if (await store.GetAsync(EndpointId, name, cancellationToken) is not null)
            ModelState.AddModelError("Form.Name", $"There's already a stack named {name}. Choose another name.");
        else if (Client is { } client && await RunsOutsideAsync(client, name, cancellationToken) is { } outside)
            ModelState.AddModelError("Form.Name", outside);
        Check(choices);

        await PreviewAsync(choices, cancellationToken);
        if (!ModelState.IsValid || Errors.Count > 0)
        {
            if (Errors.Count > 0)
                ModelState.AddModelError("", "Some choices need fixing: see the fields marked below.");
            return Page();
        }
        if (Preview is not { } content)
        {
            ModelState.AddModelError("", FileError ?? "The template's files couldn't be made.");
            return Page();
        }

        var origin = $"{Template.Title} ({Item!.Source.Name})";
        try
        {
            await store.CreateAsync(EndpointId, name, content, adopted: false, StackKind, cancellationToken,
                template: origin.Length > StackDefinition.MaxTemplate ? origin[..StackDefinition.MaxTemplate] : origin);
        }
        catch (StackRejectedException ex)
        {
            ModelState.AddModelError("Form.Name", ex.Message);
            return Page();
        }

        Flash.Success(TempData, $"Saved {name} from the template {Template.Title}. Check its files, then deploy it.");
        return RedirectToPage("/Engine/Stacks/Editor", new { endpointId = EndpointId, stackName = name, review = "true" });
    }

    private void Check(TemplateChoices choices) =>
        Errors = TemplateCompose.Problems(Template, choices).GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.First().Message);

    private async Task PreviewAsync(TemplateChoices choices, CancellationToken cancellationToken)
    {
        if (Template.Unavailable is not null)
            return;
        // Values that can't be written would make a misleading preview.
        if (Errors.Count > 0)
            choices = choices with
            {
                Variables = choices.Variables.ToDictionary(v => v.Key, v => Errors.ContainsKey($"Variables[{v.Key}]") ? "" : v.Value, StringComparer.Ordinal),
                Ports = choices.Ports.Select((p, i) => Errors.ContainsKey($"Ports[{i}]") ? p with { Host = null } : p).ToList(),
                Volumes = choices.Volumes.Select((v, i) => Errors.ContainsKey($"Volumes[{i}]") ? v with { Source = null } : v).ToList(),
            };

        if (Template.Kind == TemplateKind.Container)
            Preview = TemplateCompose.ForContainer(Template, choices, Item!.Source.Name);
        else
        {
            try
            {
                Preview = TemplateCompose.ForStack(Template, choices, await catalog.StackFileAsync(Template, cancellationToken));
            }
            catch (TemplateDownloadException ex)
            {
                FileError = $"Couldn't download the template's compose file: {ex.Message}";
                return;
            }
        }
        Risks = TemplateRisks.Find(Preview.Compose);
    }

    /// <summary>The template's name as a stack name, made unique among this endpoint's stacks when the page opens.</summary>
    private async Task<string> DefaultNameAsync(CancellationToken cancellationToken)
    {
        var name = (Template.ServiceName ?? Template.Key).ToLowerInvariant().Replace('.', '-');
        if (name.Length > StackNames.MaxLength - 3)
            name = name[..(StackNames.MaxLength - 3)].TrimEnd('-', '_');
        if (StackNames.Problem(name) is not null)
            name = "app";
        var taken = (await store.ListAsync(EndpointId, cancellationToken)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate) && n < 100; n++)
            candidate = $"{name}-{n}";
        return candidate;
    }

    private async Task<string?> RunsOutsideAsync(Docker.DockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            if ((await StackPageModel.LoadProjectAsync(client, name, cancellationToken)).Containers.Count > 0)
                return $"A Compose project named {name} already runs on {Connection.Name}. Choose another name.";
            if (StackKind == StackKind.Swarm && (await StackPageModel.LoadSwarmAsync(client, name, cancellationToken)).Services.Count > 0)
                return $"A swarm stack named {name} already runs on this swarm. Choose another name.";
            return null;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return null;
        }
    }
}
