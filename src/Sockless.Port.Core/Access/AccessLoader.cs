using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Access;

/// <summary>Reads a signed-in user's access from the database.</summary>
public sealed class AccessLoader(PortDbContext db, IOptions<IdentityOptions> identity)
{
    /// <summary>
    /// The user's access, or null when the session no longer counts: the user was removed or
    /// disabled, or their security stamp changed (password reset, roles changed by an administrator).
    /// </summary>
    public async Task<UserAccess?> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var claims = identity.Value.ClaimsIdentity;
        if (principal.FindFirstValue(claims.UserIdClaimType) is not { } id)
            return null;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.UserName, u.IsAdmin, u.DefaultRole, u.IsDisabled, u.SecurityStamp, u.TimeDisplay })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.IsDisabled)
            return null;
        if (principal.FindFirstValue(claims.SecurityStampClaimType) != user.SecurityStamp)
            return null;

        var grants = await db.EndpointGrants.AsNoTracking()
            .Where(g => g.UserId == id)
            .ToDictionaryAsync(g => g.EndpointId, g => g.Role, cancellationToken);
        return new UserAccess(id, user.UserName ?? "", user.IsAdmin, user.DefaultRole, grants, user.TimeDisplay);
    }
}
