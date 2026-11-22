using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Pages.Users;

/// <summary>The access part of the user forms: administrator, default role, and a role per endpoint.</summary>
public sealed class AccessForm
{
    public bool IsAdmin { get; set; }

    /// <summary>New users start with no access: the administrator chooses.</summary>
    public EndpointRole DefaultRole { get; set; } = EndpointRole.None;

    /// <summary>Per endpoint id; posted as <c>Access.Grants[3]</c>. Empty (null) and missing endpoints follow the default.</summary>
    public Dictionary<int, EndpointRole?> Grants { get; set; } = [];

    public AccessInput ToInput() => new(IsAdmin, DefaultRole,
        Grants.Where(g => g.Value is not null).ToDictionary(g => g.Key, g => g.Value!.Value));

    public static AccessForm From(UserSummary user) => new()
    {
        IsAdmin = user.User.IsAdmin,
        DefaultRole = user.User.DefaultRole,
        Grants = user.Grants.ToDictionary(g => g.Key, g => (EndpointRole?)g.Value),
    };

    /// <summary>The endpoint's own role; null when it follows the default.</summary>
    public EndpointRole? GrantFor(EngineConnection endpoint) => Grants.GetValueOrDefault(endpoint.Id);
}

/// <summary>What the access fields partial needs.</summary>
public sealed record AccessFieldsView(AccessForm Form, IReadOnlyList<EngineConnection> Endpoints, bool IsSelf);

public static class UserDisplay
{
    /// <summary>"Administrator", "Operator on all endpoints", "Viewer, Operator on 2".</summary>
    public static string Summary(UserSummary user, IReadOnlyList<EngineConnection> endpoints)
    {
        if (user.User.IsAdmin)
            return "Administrator";
        var access = new UserAccess(user.User.Id, "", false, user.User.DefaultRole, user.Grants);
        var roles = endpoints.Select(e => access.RoleFor(e.Id)).ToList();
        if (roles.Count == 0)
            return user.User.DefaultRole.Label();
        var groups = roles.GroupBy(r => r).OrderByDescending(g => g.Key).ToList();
        if (groups is [var only])
            return only.Key == EndpointRole.None ? "No access" : $"{only.Key.Label()} on all endpoints";
        return string.Join(", ", groups.Where(g => g.Key != EndpointRole.None).Select(g => $"{g.Key.Label()} on {g.Count()}"));
    }
}
