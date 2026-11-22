using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sockless.Port.Data;

namespace Sockless.Port.Access;

/// <summary>A user as the user list shows them.</summary>
/// <param name="Logins">Single sign-on providers linked to the user.</param>
public sealed record UserSummary(PortUser User, bool HasPassword, IReadOnlyList<string> Logins, IReadOnlyDictionary<int, EndpointRole> Grants);

/// <summary>Who may do what, as the user form sets it.</summary>
/// <param name="Grants">Roles for single endpoints; endpoints left out get <paramref name="DefaultRole"/>.</param>
public sealed record AccessInput(bool IsAdmin, EndpointRole DefaultRole, IReadOnlyDictionary<int, EndpointRole> Grants);

/// <summary>A problem with what was entered. <see cref="Field"/> is the form field it belongs to, or empty.</summary>
public sealed record UserError(string Field, string Message);

/// <summary>
/// Creates, changes and removes users. Access is read for every request, so role changes apply
/// at once; disabling a user or setting their password also changes their security stamp, which
/// ends their sessions. There's always at least one enabled administrator, and administrators
/// can't lock themselves out.
/// </summary>
public sealed class UserAdmin(UserManager<PortUser> users, PortDbContext db)
{
    public const int MaxNameLength = 64;

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);
        var logins = (await db.UserLogins.AsNoTracking().ToListAsync(cancellationToken))
            .ToLookup(l => l.UserId, l => l.ProviderDisplayName ?? l.LoginProvider);
        var grants = (await db.EndpointGrants.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(g => g.UserId);
        return [.. all.Select(u => new UserSummary(u, u.PasswordHash is not null, [.. logins[u.Id]],
            grants[u.Id].ToDictionary(g => g.EndpointId, g => g.Role)))];
    }

    public async Task<UserSummary?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return null;
        var logins = await db.UserLogins.AsNoTracking().Where(l => l.UserId == id)
            .Select(l => l.ProviderDisplayName ?? l.LoginProvider).ToListAsync(cancellationToken);
        var grants = await db.EndpointGrants.AsNoTracking().Where(g => g.UserId == id).ToDictionaryAsync(g => g.EndpointId, g => g.Role, cancellationToken);
        return new UserSummary(user, user.PasswordHash is not null, logins, grants);
    }

    /// <param name="password">Null for a user who signs in only through single sign-on.</param>
    public async Task<(string? Id, IReadOnlyList<UserError> Errors)> CreateAsync(string userName, string? email, string? password, AccessInput access,
        CancellationToken cancellationToken = default)
    {
        userName = userName.Trim();
        if (NameProblem(userName) is { } problem)
            return (null, [new("UserName", problem)]);
        if (await users.FindByNameAsync(userName) is not null)
            return (null, [new("UserName", $"There's already a user named {userName}.")]);

        var user = new PortUser
        {
            UserName = userName,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            EmailConfirmed = !string.IsNullOrWhiteSpace(email),
            IsAdmin = access.IsAdmin,
            DefaultRole = access.IsAdmin ? EndpointRole.None : access.DefaultRole,
        };
        var result = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!result.Succeeded)
            return (null, Errors(result));

        await ReplaceGrantsAsync(user.Id, access, cancellationToken);
        return (user.Id, []);
    }

    public async Task<IReadOnlyList<UserError>> UpdateAsync(string id, string? email, AccessInput access, string actingUserId,
        CancellationToken cancellationToken = default)
    {
        if (await users.FindByIdAsync(id) is not { } user)
            return [new("", "That user no longer exists.")];
        if (user.IsAdmin && !access.IsAdmin)
        {
            if (id == actingUserId)
                return [new("Admin", "You can't remove your own administrator role. Another administrator can.")];
            if (await IsLastAdminAsync(user, cancellationToken))
                return [new("Admin", "This is the only enabled administrator. Make someone else one first.")];
        }

        user.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        user.NormalizedEmail = users.NormalizeEmail(user.Email);
        user.EmailConfirmed = user.Email is not null;
        user.IsAdmin = access.IsAdmin;
        user.DefaultRole = access.IsAdmin ? EndpointRole.None : access.DefaultRole;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return Errors(result);

        await ReplaceGrantsAsync(id, access, cancellationToken);
        return [];
    }

    public async Task<IReadOnlyList<UserError>> SetDisabledAsync(string id, bool disabled, string actingUserId, CancellationToken cancellationToken = default)
    {
        if (await users.FindByIdAsync(id) is not { } user)
            return [new("", "That user no longer exists.")];
        if (disabled && id == actingUserId)
            return [new("", "You can't disable yourself.")];
        if (disabled && user.IsAdmin && await IsLastAdminAsync(user, cancellationToken))
            return [new("", "This is the only enabled administrator, so it can't be disabled.")];

        user.IsDisabled = disabled;
        var result = await users.UpdateAsync(user);
        if (result.Succeeded && disabled)
            result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded ? [] : Errors(result);
    }

    /// <summary>Sets a new password and ends the user's sessions. Also clears a lockout.</summary>
    public async Task<IReadOnlyList<UserError>> SetPasswordAsync(string id, string password)
    {
        if (await users.FindByIdAsync(id) is not { } user)
            return [new("", "That user no longer exists.")];

        foreach (var validator in users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(users, user, password);
            if (!check.Succeeded)
                return [.. check.Errors.Select(e => new UserError("Password", e.Description))];
        }

        var result = await users.RemovePasswordAsync(user);
        if (result.Succeeded)
            result = await users.AddPasswordAsync(user, password);
        if (result.Succeeded)
            result = await users.SetLockoutEndDateAsync(user, null);
        if (result.Succeeded)
            result = await users.ResetAccessFailedCountAsync(user);
        if (result.Succeeded)
            result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded ? [] : Errors(result, "Password");
    }

    /// <summary>Unlinks a single sign-on login. A user with no password and no login left couldn't sign in, so that's refused.</summary>
    public async Task<IReadOnlyList<UserError>> RemoveLoginAsync(string id, string provider)
    {
        if (await users.FindByIdAsync(id) is not { } user)
            return [new("", "That user no longer exists.")];
        var logins = await users.GetLoginsAsync(user);
        var login = logins.FirstOrDefault(l => l.LoginProvider == provider);
        if (login is null)
            return [];
        if (logins.Count == 1 && !await users.HasPasswordAsync(user))
            return [new("", "This is the user's only way to sign in. Set a password first.")];
        var result = await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        return result.Succeeded ? [] : Errors(result);
    }

    public async Task<IReadOnlyList<UserError>> DeleteAsync(string id, string actingUserId, CancellationToken cancellationToken = default)
    {
        if (await users.FindByIdAsync(id) is not { } user)
            return [];
        if (id == actingUserId)
            return [new("", "You can't remove yourself.")];
        if (user.IsAdmin && await IsLastAdminAsync(user, cancellationToken))
            return [new("", "This is the only enabled administrator, so it can't be removed.")];
        var result = await users.DeleteAsync(user);
        return result.Succeeded ? [] : Errors(result);
    }

    public static string? NameProblem(string name)
    {
        if (name.Length is < 2 or > MaxNameLength)
            return $"Use 2 to {MaxNameLength} characters.";
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-'))
            return "Use letters, digits and . _ @ - only.";
        return null;
    }

    private async Task<bool> IsLastAdminAsync(PortUser user, CancellationToken cancellationToken) =>
        !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsAdmin && !u.IsDisabled, cancellationToken);

    private async Task ReplaceGrantsAsync(string userId, AccessInput access, CancellationToken cancellationToken)
    {
        await db.EndpointGrants.Where(g => g.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (access.IsAdmin)
            return;
        var endpoints = await db.Endpoints.Select(e => e.Id).ToListAsync(cancellationToken);
        foreach (var (endpointId, role) in access.Grants)
        {
            if (endpoints.Contains(endpointId))
                db.EndpointGrants.Add(new EndpointGrant { UserId = userId, EndpointId = endpointId, Role = role });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<UserError> Errors(IdentityResult result, string field = "") =>
        [.. result.Errors.Select(e => new UserError(e.Code.Contains("Password", StringComparison.Ordinal) ? "Password" : e.Code.Contains("UserName", StringComparison.Ordinal) ? "UserName" : field, e.Description))];
}
