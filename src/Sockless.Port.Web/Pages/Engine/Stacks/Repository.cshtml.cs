using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>
/// Where a stack's files come from: connects a managed stack to a git repository, changes its
/// branch, files, sign-in and checks, turns its webhook on and off, and disconnects it again.
/// Saving fetches; nothing is deployed from here.
/// </summary>
public sealed class RepositoryModel(EngineConnections connections, StackStore store, StackJobs jobs, GitForms gitForms) : StackPageModel(connections, store, jobs)
{
    /// <summary>The settings include sign-in, and the webhook's secret is shown here.</summary>
    protected override EndpointPermission ReadPermission => WritePermission;

    [BindProperty]
    public GitInput Git { get; set; } = new();

    /// <summary>The webhook's secret, right after it was made or asked for; never on a GET.</summary>
    public string? WebhookSecret { get; private set; }

    /// <summary>The address git hosts call, from the address this page was opened at.</summary>
    public string? WebhookUrl => Repository?.WebhookId is { } id ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}{GitHooks.Path(id)}" : null;

    public IReadOnlyList<SshHostKey> HostKeys => SshKeys.HostKeys(Repository?.KnownHosts);

    public IActionResult OnGet()
    {
        if (!IsManaged)
            return RedirectToDetail();
        Git = Repository is { } repository ? GitInput.From(repository) : new GitInput();
        return Page();
    }

    /// <summary>Saves the settings, then fetches with them.</summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, IsGit ? "Stacks: change repository" : "Stacks: connect repository");
        var settings = gitForms.Validate(Git, ModelState, Repository);
        if (settings is null)
            return Redisplay();
        if (Jobs.IsBusy(EndpointId, StackName))
        {
            ModelState.AddModelError("", $"Something is running on {StackName}. Save once it has finished.");
            return Redisplay();
        }

        var connecting = !IsGit;
        try
        {
            await Store.SaveRepositoryAsync(EndpointId, StackName, settings, cancellationToken);
        }
        catch (StackRejectedException ex)
        {
            ModelState.AddModelError("Git.Auth", ex.Message);
            return Redisplay();
        }
        if (await TryStartAsync(StackOperation.Fetch))
            Flash.Success(TempData, connecting
                ? $"Connected {StackName} to the repository. Fetching its files: they replace the ones saved here once the fetch succeeds."
                : $"Saved the repository settings of {StackName}. Fetching with them.");
        return RedirectToDetail();
    }

    /// <summary>Puts a new deploy key on the form, to add to the repository before saving.</summary>
    [NoAudit]
    public IActionResult OnPostGenerateKey()
    {
        if (!IsManaged)
            return RedirectToDetail();
        gitForms.Generate(Git, $"sockless-port {StackName}@{Connection.Name}");
        ModelState.Clear();
        Git.Existing = Repository;
        Git.HasSecret = Repository?.ProtectedSecret is not null;
        return Page();
    }

    /// <summary>Turns the webhook on with a new address and secret, replacing the old ones. The secret is shown once, now.</summary>
    public async Task<IActionResult> OnPostWebhookAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, Repository.HasWebhook ? "Stacks: replace webhook" : "Stacks: turn on webhook");
        WebhookSecret = await Store.SetWebhookAsync(Repository.StackId, enabled: true, cancellationToken);
        return await ReloadAsync(cancellationToken);
    }

    /// <summary>Shows the webhook's secret again, to set it up on another host.</summary>
    public async Task<IActionResult> OnPostShowWebhookSecretAsync(CancellationToken cancellationToken)
    {
        if (Repository is not { HasWebhook: true } repository)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, "Stacks: show webhook secret");
        try
        {
            WebhookSecret = Store.WebhookSecret(repository);
        }
        catch (GitException ex)
        {
            Flash.Error(TempData, ex.Message + " Replace the webhook to get a new secret.");
            return RedirectToSelf();
        }
        return await ReloadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostWebhookOffAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, "Stacks: turn off webhook");
        await Store.SetWebhookAsync(Repository.StackId, enabled: false, cancellationToken);
        Flash.Success(TempData, $"Turned the webhook of {StackName} off. Calls to its address now fail.");
        return RedirectToSelf();
    }

    /// <summary>Forgets the SSH server's host keys: the next connection trusts whatever it presents.</summary>
    public async Task<IActionResult> OnPostForgetHostKeysAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, "Stacks: forget host keys");
        await Store.ForgetHostKeysAsync(Repository.StackId, cancellationToken);
        Flash.Success(TempData, "Forgot the host keys. The next fetch trusts the keys the server presents, and keeps them.");
        return RedirectToSelf();
    }

    /// <summary>The stack stops following the repository; its files stay and become editable.</summary>
    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
            return RedirectToDetail();
        AuditTrail.Action(HttpContext, "Stacks: disconnect repository");
        if (Jobs.IsBusy(EndpointId, StackName))
        {
            Flash.Error(TempData, $"Something is running on {StackName}. Disconnect it once that has finished.");
            return RedirectToSelf();
        }
        await Store.DisconnectRepositoryAsync(EndpointId, StackName, cancellationToken);
        Flash.Success(TempData, $"{StackName} no longer follows its repository. Its files stay as they were fetched, and can be edited here.");
        return RedirectToDetail();
    }

    /// <summary>The page again after a change, with the stored settings (the secret answer must not be cached).</summary>
    private async Task<IActionResult> ReloadAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await LoadAsync(cancellationToken);
        Git = Repository is { } repository ? GitInput.From(repository) : new GitInput();
        ModelState.Clear();
        return Page();
    }

    private PageResult Redisplay()
    {
        Git.Existing = Repository;
        Git.HasSecret = Repository?.ProtectedSecret is not null;
        gitForms.Generated(Git);
        return Page();
    }

    private RedirectToPageResult RedirectToSelf() => RedirectToPage("/Engine/Stacks/Repository", new { endpointId = EndpointId, stackName = StackName });

    private RedirectToPageResult RedirectToDetail() => RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = StackName });
}
