using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

/// <summary>
/// Accepting the risk of one finding, with the reason. For this image needs the operator role;
/// for every image an administrator, since it hides the finding on every endpoint.
/// </summary>
public sealed class AcceptRiskModel(EngineConnections connections, ImageUpdates updates, ImageScans scans, AcceptedRisks risks)
    : ImagePageModel(connections, updates)
{
    /// <summary>The form only makes sense for someone who may accept.</summary>
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    [BindProperty(SupportsGet = true)]
    public string? Vulnerability { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Package { get; set; }

    /// <summary>Where it was found; empty for OS packages.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Target { get; set; }

    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty]
    public bool EveryImage { get; set; }

    [BindProperty]
    public int ExpiresInDays { get; set; } = 90;

    /// <summary>The finding from the image's last scan.</summary>
    public VulnerabilityFinding? Finding { get; private set; }

    public string? Os { get; private set; }

    private string FullId => Image?.Id ?? ImageScans.Normalize(ImageId);

    private RiskKey Key => new(Vulnerability ?? "", Package ?? "", string.IsNullOrEmpty(Target) ? null : Target);

    public override string? AuditTarget => $"{Vulnerability} in {Package}{(EveryImage ? " (every image)" : $" ({DisplayName})")}";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadFindingAsync(cancellationToken) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Images: accept risk");
        if (EveryImage && !Access.IsAdmin)
        {
            AuditTrail.Deny(HttpContext, "Accepting a risk for every image needs an administrator.");
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        var result = await risks.AcceptAsync(new AcceptRiskRequest(FullId, Tags.FirstOrDefault(), Key, EveryImage, ExpiresInDays, Reason),
            Access.UserName, cancellationToken);
        switch (result)
        {
            case AcceptedRisk risk:
                Flash.Success(TempData, $"Accepted the risk of {risk.VulnerabilityId} in {risk.Package}. It no longer counts{(risk.ImageId is null ? " in any image" : "")}.");
                return RedirectToPage("/Engine/Images/AcceptedRisk", new { endpointId = EndpointId, imageId = ImageId, riskId = risk.Id });
            case RiskRefused refused:
                ModelState.AddModelError("", refused.Reason);
                break;
            case null:
                ModelState.AddModelError("", "The risk wasn't accepted.");
                break;
        }
        return await LoadFindingAsync(cancellationToken) ? Page() : NotFound();
    }

    private async Task<bool> LoadFindingAsync(CancellationToken cancellationToken)
    {
        var scan = await scans.GetAsync(FullId, cancellationToken);
        Os = scan?.Summary.Os;
        Finding = scan?.Findings.FirstOrDefault(f => f.Key == Key);
        return Finding is not null;
    }
}
