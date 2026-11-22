using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

/// <summary>
/// An accepted risk, seen from an image it holds for: why, by whom, until when, and its comments.
/// Viewers read it; operators comment and revoke (an administrator, for every image).
/// </summary>
public sealed class AcceptedRiskModel(EngineConnections connections, ImageUpdates updates, AcceptedRisks risks, TimeProvider time)
    : ImagePageModel(connections, updates)
{
    public AcceptedRisk Risk { get; private set; } = null!;

    [BindProperty]
    public string? Comment { get; set; }

    public DateTimeOffset Now { get; } = time.GetUtcNow();

    public bool Active => Risk.IsActive(Now);

    /// <summary>Revoking one that holds for every image needs an administrator, like accepting it.</summary>
    public bool CanRevoke => CanWrite && (Risk.ImageId is not null || Access.IsAdmin);

    public override string? AuditTarget => Risk is null ? null : $"{Risk.VulnerabilityId} in {Risk.Package}";

    private string FullId => Image?.Id ?? ImageScans.Normalize(ImageId);

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (await base.LoadAsync(cancellationToken) is { } stop)
            return stop;
        if (!int.TryParse(RouteData.Values["riskId"]?.ToString(), out var id) || await risks.GetAsync(id, cancellationToken) is not { } risk
            || (risk.ImageId is not null && risk.ImageId != FullId))
            return NotFound();
        Risk = risk;
        return null;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostCommentAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Images: comment on accepted risk");
        if (await risks.CommentAsync(Risk.Id, Access.UserName, Comment, cancellationToken) is { } problem)
        {
            ModelState.AddModelError(nameof(Comment), problem);
            return Page();
        }
        Flash.Success(TempData, "Added the comment.");
        return RedirectToPage(new { endpointId = EndpointId, imageId = ImageId, riskId = Risk.Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Images: revoke accepted risk");
        if (!CanRevoke)
        {
            AuditTrail.Deny(HttpContext, "Revoking an accepted risk for every image needs an administrator.");
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        await risks.RevokeAsync(Risk.Id, cancellationToken);
        Flash.Success(TempData, $"Revoked the acceptance. {Risk.VulnerabilityId} in {Risk.Package} counts again.");
        return RedirectToPage("/Engine/Images/Vulnerabilities", new { endpointId = EndpointId, imageId = ImageId });
    }
}
