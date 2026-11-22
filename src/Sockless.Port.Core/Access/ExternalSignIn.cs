using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Access;

/// <summary>Who a single sign-on login is, or why they can't sign in.</summary>
public abstract record ExternalSignInResult
{
    private ExternalSignInResult() { }

    public sealed record Found(PortUser User, bool Linked, bool Created) : ExternalSignInResult;

    public sealed record Refused(string Reason) : ExternalSignInResult;
}

/// <summary>
/// Matches a login from the OpenID Connect provider to a user: one already linked to it, else an
/// existing user with the same verified email address (then linked), else a new user when
/// <see cref="OidcOptions.CreateUsers"/> allows it.
/// </summary>
public sealed class ExternalSignIn(UserManager<PortUser> users, PortDbContext db, IOptions<PortOptions> options)
{
    public async Task<ExternalSignInResult> ResolveAsync(UserLoginInfo login, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (await users.FindByLoginAsync(login.LoginProvider, login.ProviderKey) is { } linked)
            return linked.IsDisabled ? Disabled() : new ExternalSignInResult.Found(linked, Linked: false, Created: false);

        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        var verified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (email is { Length: > 0 } && verified)
        {
            var normalized = users.NormalizeEmail(email);
            var matches = await db.Users.Where(u => u.NormalizedEmail == normalized).ToListAsync(cancellationToken);
            if (matches.Count > 1)
                return new ExternalSignInResult.Refused("More than one user has your email address here. Ask an administrator to fix that.");
            if (matches is [var match])
            {
                if (match.IsDisabled)
                    return Disabled();
                // A user keeps one login per provider: a second identity at the provider can't take this one over.
                if ((await users.GetLoginsAsync(match)).Any(l => l.LoginProvider == login.LoginProvider))
                    return new ExternalSignInResult.Refused("Your email address belongs to a user who signs in with another account at the provider.");
                var added = await users.AddLoginAsync(match, login);
                return added.Succeeded
                    ? new ExternalSignInResult.Found(match, Linked: true, Created: false)
                    : new ExternalSignInResult.Refused(string.Join(" ", added.Errors.Select(e => e.Description)));
            }
        }

        var oidc = options.Value.Oidc;
        if (!oidc.CreateUsers)
            return new ExternalSignInResult.Refused(email is null || !verified
                ? "There's no user here for your account. Ask an administrator to add you with your email address, and make sure the provider shares a verified email."
                : "There's no user here for your account. Ask an administrator to add you with your email address.");

        var name = await FreeUserNameAsync(principal, email);
        var user = new PortUser
        {
            UserName = name,
            Email = verified ? email : null,
            EmailConfirmed = verified && email is not null,
            DefaultRole = oidc.DefaultRole > EndpointRole.Operator ? EndpointRole.Operator : oidc.DefaultRole,
        };
        var created = await users.CreateAsync(user);
        if (created.Succeeded)
            created = await users.AddLoginAsync(user, login);
        return created.Succeeded
            ? new ExternalSignInResult.Found(user, Linked: true, Created: true)
            : new ExternalSignInResult.Refused(string.Join(" ", created.Errors.Select(e => e.Description)));
    }

    private static ExternalSignInResult.Refused Disabled() => new("Your user is disabled. Ask an administrator.");

    /// <summary>The provider's preferred user name, made valid here and unique.</summary>
    private async Task<string> FreeUserNameAsync(ClaimsPrincipal principal, string? email)
    {
        var wanted = principal.FindFirstValue("preferred_username") ?? email ?? principal.FindFirstValue(ClaimTypes.Name) ?? "user";
        var cleaned = new string([.. wanted.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-')]);
        if (cleaned.Length < 2)
            cleaned = "user";
        if (cleaned.Length > UserAdmin.MaxNameLength - 4)
            cleaned = cleaned[..(UserAdmin.MaxNameLength - 4)];

        var name = cleaned;
        for (var i = 2; await users.FindByNameAsync(name) is not null; i++)
            name = $"{cleaned}-{i}";
        return name;
    }
}
