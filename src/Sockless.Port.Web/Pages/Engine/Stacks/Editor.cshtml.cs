using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>
/// Edits a managed stack's files. Deploying goes through a review: the changes against what was
/// last deployed, and Compose's validation. A deploy only goes ahead for the files that were reviewed.
/// </summary>
public sealed class EditorModel(EngineConnections connections, StackStore store, StackJobs jobs, StackValidator validator)
    : StackPageModel(connections, store, jobs)
{
    [BindProperty]
    public StackInput Input { get; set; } = new();

    /// <summary>The hash of the content that was reviewed (<see cref="StackReview.Hash"/>).</summary>
    [BindProperty]
    public string? Reviewed { get; set; }

    /// <summary>Set once the changes were reviewed.</summary>
    public StackReview? Review { get; private set; }

    public StackValidationView? Validation { get; private set; }

    /// <summary>Why the review is shown again instead of deploying.</summary>
    public string? Notice { get; private set; }

    /// <param name="review">Open with the saved files reviewed, ready to deploy.</param>
    public async Task<IActionResult> OnGetAsync(bool review, CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        var saved = await Store.Files.ReadAsync(EndpointId, StackName, cancellationToken) ?? StackContent.Empty;
        Input = new StackInput { Name = StackName, Compose = saved.Compose, Env = saved.Env };
        if (review)
            await ReviewAsync(saved, cancellationToken);
        return Page();
    }

    /// <summary>Checks the files with docker compose config: the result alone for htmx, the whole form otherwise.</summary>
    public async Task<IActionResult> OnPostValidateAsync(CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        Validation = await StackForms.ValidateAsync(validator, Store, Connection, StackName, Input.Content, cancellationToken);
        return Request.Headers.ContainsKey("HX-Request") ? Partial("_StackValidation", Validation) : Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        var content = Input.Content;
        if (!await CheckAsync(content, cancellationToken))
            return Page();
        await Store.SaveAsync(EndpointId, StackName, content, cancellationToken);
        Flash.Success(TempData, Definition!.DeployedAt is null
            ? $"Saved {StackName}. It isn't deployed yet."
            : $"Saved {StackName}. Deploy it to apply the changes.");
        return RedirectToPage("/Engine/Stacks/Editor", new { endpointId = EndpointId, stackName = StackName });
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        await ReviewAsync(Input.Content, cancellationToken);
        return Page();
    }

    /// <param name="pull">Pull every image again first (an update).</param>
    public async Task<IActionResult> OnPostDeployAsync(bool pull, CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return RedirectToDetail();
        var content = Input.Content;
        if (Reviewed != StackReview.Hash(content))
        {
            Notice = "The files changed after the review. Check the changes again before deploying.";
            await ReviewAsync(content, cancellationToken);
            return Page();
        }
        if (!await CheckAsync(content, cancellationToken))
            return Page();
        if (Jobs.IsBusy(EndpointId, StackName))
        {
            Notice = $"Something is already running on {StackName}. Wait for it to finish, then deploy.";
            await ReviewAsync(content, cancellationToken);
            return Page();
        }

        await Store.SaveAsync(EndpointId, StackName, content, cancellationToken);
        try
        {
            await Jobs.StartAsync(Connection, StackName, pull ? StackOperation.Update : StackOperation.Deploy);
        }
        catch (StackJobRejectedException ex)
        {
            Flash.Error(TempData, $"Saved {StackName}, but it wasn't deployed: {ex.Message}");
        }
        return RedirectToDetail();
    }

    private RedirectToPageResult RedirectToDetail() => RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = StackName });

    /// <summary>Validates; on failure the problems are on the page and the caller shows it again.</summary>
    private async Task<bool> CheckAsync(StackContent content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content.Compose))
        {
            ModelState.AddModelError("Input.Compose", "The compose file is empty. Remove the stack instead if it's no longer needed.");
            return false;
        }
        Validation = await StackForms.ValidateAsync(validator, Store, Connection, StackName, content, cancellationToken);
        if (Validation.Check is { Valid: false })
        {
            ModelState.AddModelError("Input.Compose", "Compose rejected the file. Fix the problems listed above it.");
            return false;
        }
        return true;
    }

    private async Task ReviewAsync(StackContent content, CancellationToken cancellationToken)
    {
        Input = new StackInput { Name = StackName, Compose = content.Compose, Env = content.Env };
        if (!await CheckAsync(content, cancellationToken))
            return;
        var deployed = await Store.Files.ReadDeployedAsync(EndpointId, StackName, cancellationToken);
        Review = StackReview.Between(deployed, content);
        Reviewed = StackReview.Hash(content);
        ModelState.Remove(nameof(Reviewed));
    }
}
