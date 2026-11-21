using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;

namespace Sockless.Port.Setup;

/// <summary>
/// Tracks whether first-run setup is still needed. Until the first administrator exists, every
/// request is sent to the setup page, and creating that account needs a one-time token that is
/// written only to the server log. This stops whoever reaches a fresh instance first from
/// claiming it.
/// </summary>
public sealed partial class SetupState(ILogger<SetupState> logger)
{
    private readonly Lock _gate = new();
    private string? _token;
    private volatile bool _complete;

    /// <summary>True once at least one user exists. Never goes back to false.</summary>
    public bool IsComplete => _complete;

    public async Task InitializeAsync(PortDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            MarkComplete();
            return;
        }

        string token;
        lock (_gate)
        {
            token = _token ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        LogSetupToken(logger, token);
    }

    /// <summary>The current setup token, or null once setup is complete. Exposed for tests.</summary>
    internal string? Token
    {
        get
        {
            lock (_gate)
            {
                return _complete ? null : _token;
            }
        }
    }

    public bool ValidateToken(string? candidate)
    {
        var expected = Token;
        if (expected is null || string.IsNullOrWhiteSpace(candidate))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(candidate.Trim().ToLowerInvariant()));
    }

    /// <summary>Creates the first administrator. Fails if setup already ran or the token is wrong.</summary>
    public async Task<IdentityResult> CreateAdministratorAsync(
        UserManager<PortUser> users, string token, string userName, string password)
    {
        if (!ValidateToken(token))
            return IdentityResult.Failed(new IdentityError { Code = "InvalidSetupToken", Description = "The setup token is not valid." });

        // Re-check the database: another request may have completed setup in the meantime.
        if (await users.Users.AnyAsync())
        {
            MarkComplete();
            return IdentityResult.Failed(new IdentityError { Code = "SetupComplete", Description = "Setup has already been completed." });
        }

        var result = await users.CreateAsync(new PortUser { UserName = userName }, password);
        if (result.Succeeded)
            MarkComplete();
        return result;
    }

    private void MarkComplete()
    {
        lock (_gate)
        {
            _complete = true;
            _token = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No users exist yet. Open Sockless Port in a browser and enter this setup token to create the first administrator: {SetupToken}")]
    private static partial void LogSetupToken(ILogger logger, string setupToken);
}
