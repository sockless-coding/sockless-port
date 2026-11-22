using Microsoft.AspNetCore.Identity;

namespace Sockless.Port.Data;

public sealed class PortUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
