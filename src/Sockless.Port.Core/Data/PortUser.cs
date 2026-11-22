using Microsoft.AspNetCore.Identity;
using Sockless.Port.Access;
using Sockless.Port.Settings;

namespace Sockless.Port.Data;

public sealed class PortUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Administrators manage users, endpoints, registries and settings, and have every role on every endpoint.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>The role on endpoints without a grant of their own, including endpoints added later.</summary>
    public EndpointRole DefaultRole { get; set; }

    /// <summary>A disabled user can't sign in, and their sessions end at their next request.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>How the user wants times shown; null for the server's default.</summary>
    public TimeDisplay? TimeDisplay { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}
