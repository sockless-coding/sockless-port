using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Access;

/// <summary>Identity's sign-in, refusing disabled users (password and single sign-on alike).</summary>
public sealed class PortSignInManager(
    UserManager<PortUser> users, IHttpContextAccessor context, IUserClaimsPrincipalFactory<PortUser> claims,
    IOptions<IdentityOptions> options, ILogger<SignInManager<PortUser>> logger, IAuthenticationSchemeProvider schemes,
    IUserConfirmation<PortUser> confirmation)
    : SignInManager<PortUser>(users, context, claims, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(PortUser user) => !user.IsDisabled && await base.CanSignInAsync(user);
}
