using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Data;

namespace Sockless.Port.Vulnerabilities;

/// <summary>Accepting the risk of one finding.</summary>
/// <param name="ImageId">The image it was found in (<c>sha256:…</c>).</param>
/// <param name="EveryImage">Hold for every image with this finding, not only this one.</param>
/// <param name="ExpiresInDays">When to look again; 0 holds until revoked. One of <see cref="AcceptedRisks.ExpiryChoices"/>.</param>
/// <param name="Reason">Why it's acceptable: the first comment.</param>
public sealed record AcceptRiskRequest(string ImageId, string? ImageName, RiskKey Key, bool EveryImage, int ExpiresInDays, string? Reason);

/// <summary>Why a risk wasn't accepted, for people.</summary>
public sealed record RiskRefused(string Reason);

/// <summary>Either the accepted risk or why it wasn't accepted. Switch on it exhaustively.</summary>
public union AcceptRiskResult(AcceptedRisk, RiskRefused);

/// <summary>
/// Accepted risks and their comments. A finding stays in the scan; an accepted one only leaves the
/// counts and badges, until the acceptance expires or is revoked. Nothing here checks roles: the
/// pages do (every image needs an administrator).
/// </summary>
public sealed class AcceptedRisks(IServiceScopeFactory scopes, TimeProvider time)
{
    public const int MaxComment = 2000;

    /// <summary>Days until an acceptance expires: never, or a month up to a year.</summary>
    public static readonly IReadOnlyList<int> ExpiryChoices = [0, 30, 90, 180, 365];

    /// <summary>Every acceptance that could hold for the image (its own and every image's), expired ones too, with their comments.</summary>
    public async Task<IReadOnlyList<AcceptedRisk>> ForImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        imageId = ImageScans.Normalize(imageId);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var risks = await db.AcceptedRisks.AsNoTracking().Include(r => r.Comments)
            .Where(r => r.ImageId == null || r.ImageId == imageId)
            .ToListAsync(cancellationToken);
        foreach (var risk in risks)
            risk.Comments = [.. risk.Comments.OrderBy(c => c.At).ThenBy(c => c.Id)];
        return [.. risks.OrderByDescending(r => r.CreatedAt)];
    }

    public async Task<AcceptedRisk?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var risk = await db.AcceptedRisks.AsNoTracking().Include(r => r.Comments).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (risk is not null)
            risk.Comments = [.. risk.Comments.OrderBy(c => c.At).ThenBy(c => c.Id)];
        return risk;
    }

    /// <summary>Accepts a finding that's in the image's last scan, unless an acceptance already holds for it.</summary>
    public async Task<AcceptRiskResult> AcceptAsync(AcceptRiskRequest request, string userName, CancellationToken cancellationToken = default)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            return new RiskRefused("Say why the risk is acceptable. It's the first comment, so others know why the finding is hidden.");
        if (reason.Length > MaxComment)
            return new RiskRefused($"Keep the reason under {MaxComment} characters.");
        if (!ExpiryChoices.Contains(request.ExpiresInDays))
            return new RiskRefused("Choose when the acceptance expires from the list.");

        var imageId = ImageScans.Normalize(request.ImageId);
        var key = request.Key;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var found = await db.ImageFindings.AnyAsync(f => db.ImageScans.Any(s => s.Id == f.ScanId && s.ImageId == imageId)
                                                         && f.VulnerabilityId == key.VulnerabilityId && f.Package == key.Package && f.Target == key.Target,
            cancellationToken);
        if (!found)
            return new RiskRefused("That finding isn't in the image's last scan any more.");

        var now = time.GetUtcNow();
        var existing = await db.AcceptedRisks.AsNoTracking()
            .Where(r => r.VulnerabilityId == key.VulnerabilityId && r.Package == key.Package && r.Target == key.Target && (r.ImageId == null || r.ImageId == imageId))
            .ToListAsync(cancellationToken);
        if (existing.Any(r => r.IsActive(now)))
            return new RiskRefused("The risk of this finding is accepted already. Comment on that acceptance instead.");

        var risk = new AcceptedRisk
        {
            VulnerabilityId = key.VulnerabilityId,
            Package = key.Package,
            Target = key.Target,
            ImageId = request.EveryImage ? null : imageId,
            ImageName = request.ImageName,
            CreatedBy = userName,
            CreatedAt = now,
            ExpiresAt = request.ExpiresInDays > 0 ? now.AddDays(request.ExpiresInDays) : null,
            Comments = [new AcceptedRiskComment { UserName = userName, At = now, Text = reason }],
        };
        db.AcceptedRisks.Add(risk);
        await db.SaveChangesAsync(cancellationToken);
        return risk;
    }

    /// <returns>Why the comment wasn't added; null when it was.</returns>
    public async Task<string?> CommentAsync(int riskId, string userName, string? text, CancellationToken cancellationToken = default)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return "Write a comment first.";
        if (text.Length > MaxComment)
            return $"Keep comments under {MaxComment} characters.";
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (!await db.AcceptedRisks.AnyAsync(r => r.Id == riskId, cancellationToken))
            return "That acceptance was revoked meanwhile.";
        db.AcceptedRiskComments.Add(new AcceptedRiskComment { RiskId = riskId, UserName = userName, At = time.GetUtcNow(), Text = text });
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>Revokes an acceptance with its comments: the finding counts again.</summary>
    /// <returns>False when it was gone already.</returns>
    public async Task<bool> RevokeAsync(int riskId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.AcceptedRisks.Where(r => r.Id == riskId).ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
