using Sockless.Port.Settings;

namespace Sockless.Port.Access;

/// <summary>
/// What the signed-in user may do, read from the database for each request, so a change to their
/// roles applies at once.
/// </summary>
/// <param name="DefaultRole">The role on endpoints without a grant of their own, including endpoints added later.</param>
/// <param name="Grants">Roles for single endpoints.</param>
/// <param name="TimeDisplay">The user's choice; null for the server's default.</param>
public sealed record UserAccess(string UserId, string UserName, bool IsAdmin, EndpointRole DefaultRole, IReadOnlyDictionary<int, EndpointRole> Grants,
    TimeDisplay? TimeDisplay = null)
{
    /// <summary>Administrators are everything on every endpoint.</summary>
    public EndpointRole RoleFor(int endpointId) =>
        IsAdmin ? EndpointRole.SwarmOperator : Grants.TryGetValue(endpointId, out var role) ? role : DefaultRole;

    public bool Can(int endpointId, EndpointPermission permission) => RoleFor(endpointId).Allows(permission);

    public bool CanView(int endpointId) => Can(endpointId, EndpointPermission.View);
}
