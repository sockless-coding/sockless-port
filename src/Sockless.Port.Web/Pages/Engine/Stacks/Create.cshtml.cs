using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>
/// A new stack from a compose file, from a git repository (<c>?source=git</c>: fetched and checked
/// before it's saved), or (with <c>?adopt=name</c>) a project deployed outside Sockless Port,
/// prefilled from its compose file or its containers.
/// </summary>
public sealed class CreateModel(EngineConnections connections, StackStore store, StackJobs jobs, StackValidator validator, StackGit git, GitForms gitForms,
    IOptions<PortOptions> options) : EndpointPageModel(connections)
{
    public const string GitSourceKey = "git";

    /// <summary><c>git</c> for a stack whose files come from a repository.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    public bool FromGit => Source == GitSourceKey && !Adopting;

    [BindProperty]
    public GitInput Git { get; set; } = new();

    /// <summary>Why the repository couldn't be fetched or used.</summary>
    public string? GitError { get; private set; }

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

    /// <summary>Set when the adopted files were read from Portainer's data.</summary>
    public PortainerFiles? AdoptedFromPortainer { get; private set; }

    /// <summary>Why the project's file couldn't be used, when it was rebuilt.</summary>
    public string? AdoptNote { get; private set; }

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
            var adopted = await StackAdopter.AdoptAsync(client, project, PortainerEngines(client), cancellationToken);
            AdoptSource = adopted.Source;
            AdoptedFromPortainer = adopted.Portainer;
            AdoptNote = adopted.Note;
            Input = new StackInput { Name = Adopt, Compose = adopted.Content.Compose, Env = adopted.Content.Env };
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = $"Couldn't read the project: {EngineFailures.Describe(ex, Connection)}";
        }
        return Page();
    }

    /// <summary>Puts a new deploy key on the form, to add to the repository before saving.</summary>
    [NoAudit]
    public IActionResult OnPostGenerateKey()
    {
        if (!FromGit)
            return BadRequest();
        var name = StackNames.Problem(Input.Name?.Trim()) is null ? Input.Name!.Trim() : "stack";
        gitForms.Generate(Git, $"sockless-port {name}@{Connection.Name}");
        ModelState.Clear();
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

        if (FromGit)
            return await CreateFromGitAsync(name, kind, deploy, cancellationToken);

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

        return await CreatedAsync(name, deploy, Adopting
            ? $"Adopted {name}. Its containers keep running; deploy it from here when you change it."
            : $"Saved {name}. It isn't deployed yet.");
    }

    /// <summary>
    /// Fetches the repository into a folder of its own, checks its compose file with Compose (in
    /// the checkout, so files next to it are found), and only then saves the stack, whose checkout
    /// the folder becomes. Nothing is kept when any of it fails.
    /// </summary>
    private async Task<IActionResult> CreateFromGitAsync(string name, StackKind kind, bool deploy, CancellationToken cancellationToken)
    {
        var settings = gitForms.Validate(Git, ModelState, existing: null);
        if (!ModelState.IsValid || settings is null)
            return Page();

        var staged = Path.Combine(options.Value.TempPath, $"git-stage-{Guid.NewGuid():n}");
        try
        {
            GitFetchResult fetched;
            try
            {
                var remote = new GitRemote(settings.Url, settings.Auth, settings.Username, settings.Secret);
                fetched = await git.FetchAsync(new GitCheckoutRequest(remote, settings.Branch, settings.ComposePath, settings.EnvPath), staged, null, null, cancellationToken);
            }
            catch (Exception ex) when (ex is GitException or ComposeUnavailableException)
            {
                GitError = ex.Message;
                return Page();
            }

            var content = StackContent.Normalize(fetched.Compose, fetched.Env ?? Input.Env);
            var file = Path.GetFullPath(Path.Combine(staged, settings.ComposePath));
            Validation = await StackForms.ValidateAsync(validator, store, Connection, name, content, kind, cancellationToken,
                new StackSource(Path.GetDirectoryName(file)!, file));
            if (Validation.Check is { Valid: false })
                GitError = $"Compose rejected {settings.ComposePath} at {fetched.Commit.Short}. Fix the problems listed below in the repository, then try again.";
            else if (Validation.Check is null && deploy)
                GitError = "The files can't be checked or deployed without Compose. Create the stack without deploying for now.";
            if (GitError is not null)
                return Page();

            try
            {
                await store.CreateFromGitAsync(EndpointId, name, kind, settings, fetched, staged, Input.Env, cancellationToken);
            }
            catch (StackRejectedException ex)
            {
                ModelState.AddModelError("Input.Name", ex.Message);
                return Page();
            }
            AuditTrail.Action(HttpContext, "Stacks: create from git");
            return await CreatedAsync(name, deploy, $"Fetched {fetched.Commit.Short} ({fetched.Commit.Subject}) and saved {name}. It isn't deployed yet.");
        }
        finally
        {
            StackStore.DeleteTree(staged);
        }
    }

    private async Task<IActionResult> CreatedAsync(string name, bool deploy, string saved)
    {
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
            Flash.Success(TempData, saved);
        }
        return RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = name });
    }

    /// <summary>
    /// Where to look for the Portainer that deployed a project: this endpoint, then the other online
    /// ones the user operates (Portainer often deploys through its agent, from another host). Reading
    /// its files shows the stack's variables, so viewing an endpoint isn't enough.
    /// </summary>
    private List<PortainerSearch> PortainerEngines(Docker.DockerClient client) =>
    [
        new(Connection.Name, client),
        .. Connections.All
            .Where(c => c.Id != EndpointId && Access.Can(c.Id, EndpointPermission.Operate))
            .Select(c => (c.Name, c.Client))
            .Where(c => c.Client is not null)
            .Select(c => new PortainerSearch(c.Name, c.Client!)),
    ];

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
