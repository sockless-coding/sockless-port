using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Registries;

/// <summary>A stored credential as pages see it: never the password.</summary>
/// <param name="Readable">False when the Data Protection keys that encrypted it are gone, so it must be re-entered.</param>
public sealed record RegistryInfo(int Id, string Name, string Registry, string Username, DateTimeOffset UpdatedAt, bool Readable);

/// <summary>What a user typed into the registry form.</summary>
/// <param name="Password">Null or empty when editing keeps the stored one.</param>
public sealed record RegistryInput(string Name, string Registry, string Username, string? Password);

/// <summary>A field-level problem with <see cref="RegistryInput"/>.</summary>
public sealed record RegistryInputError(string Field, string Message);

/// <summary>
/// Registry credentials, encrypted at rest with Data Protection, and the lookup that attaches
/// them to pulls and builds by the image's registry host. Singleton: decrypted credentials are
/// cached in memory and reloaded after each change.
/// </summary>
public sealed partial class RegistryCredentials(IServiceScopeFactory scopes, IDataProtectionProvider protection, TimeProvider time, ILogger<RegistryCredentials> logger)
{
    private const string Purpose = "Sockless.Port.Registries.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile IReadOnlyDictionary<string, AuthConfig>? _cache;

    // ---------------------------------------------------------------- lookup

    /// <summary>The <c>X-Registry-Auth</c> header for pulling <paramref name="reference"/>, or null when no credential matches.</summary>
    public async Task<string?> AuthHeaderForAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (ImageReference.Parse(reference) is not { } image)
            return null;
        var all = await LoadAsync(cancellationToken);
        return all.TryGetValue(image.Registry, out var auth) ? RegistryAuth.Encode(auth) : null;
    }

    /// <summary>The <c>X-Registry-Config</c> header for builds: every readable credential. Null when there are none.</summary>
    public async Task<string?> BuildConfigAsync(CancellationToken cancellationToken = default)
    {
        var all = await LoadAsync(cancellationToken);
        return all.Count == 0 ? null : RegistryAuth.EncodeConfig(all.Values.ToDictionary(a => a.ServerAddress!, a => a, StringComparer.Ordinal));
    }

    /// <summary>The decrypted credential, for testing a login. Null if unknown or unreadable.</summary>
    public async Task<AuthConfig?> GetAuthAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var row = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null ? null : ToAuth(row);
    }

    // ---------------------------------------------------------------- management

    public async Task<IReadOnlyList<RegistryInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rows = await db.Registries.AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(Info)];
    }

    public async Task<RegistryInfo?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var row = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null ? null : Info(row);
    }

    /// <summary>Adds a credential, or with <paramref name="id"/> updates one. Returns the problems, if any; nothing is saved then.</summary>
    public async Task<IReadOnlyList<RegistryInputError>> SaveAsync(int? id, RegistryInput input, CancellationToken cancellationToken = default)
    {
        var errors = new List<RegistryInputError>();
        var name = input.Name.Trim();
        var registry = string.IsNullOrWhiteSpace(input.Registry) ? "" : ImageReference.NormalizeRegistry(input.Registry);
        var username = input.Username.Trim();
        var password = string.IsNullOrEmpty(input.Password) ? null : input.Password;

        if (name.Length is 0 or > 64)
            errors.Add(new(nameof(input.Name), "Give it a name of up to 64 characters."));
        if (!HostPattern().IsMatch(registry))
            errors.Add(new(nameof(input.Registry), "Use a registry host such as docker.io, ghcr.io or registry.example.com:5000."));
        if (username.Length is 0 or > 255)
            errors.Add(new(nameof(input.Username), "Enter the user name."));
        if (id is null && password is null)
            errors.Add(new(nameof(input.Password), "Enter the password or access token."));

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (errors.Count == 0 && await db.Registries.AnyAsync(r => r.Registry == registry && r.Id != (id ?? 0), cancellationToken))
            errors.Add(new(nameof(input.Registry), $"There are already credentials for {registry}. Edit those instead."));
        if (errors.Count > 0)
            return errors;

        var now = time.GetUtcNow();
        RegistryCredential row;
        if (id is { } existing)
        {
            row = await db.Registries.FirstOrDefaultAsync(r => r.Id == existing, cancellationToken)
                  ?? throw new KeyNotFoundException($"Registry {existing} doesn't exist.");
            row.Name = name;
            row.Registry = registry;
            row.Username = username;
            if (password is not null)
                row.ProtectedPassword = _protector.Protect(password);
            row.UpdatedAt = now;
        }
        else
        {
            row = new RegistryCredential
            {
                Name = name,
                Registry = registry,
                Username = username,
                ProtectedPassword = _protector.Protect(password!),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Registries.Add(row);
        }
        await db.SaveChangesAsync(cancellationToken);
        _cache = null;
        return [];
    }

    /// <returns>The removed credential's name, or null if it didn't exist.</returns>
    public async Task<string?> RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var row = await db.Registries.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
            return null;
        db.Registries.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        _cache = null;
        return row.Name;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IReadOnlyDictionary<string, AuthConfig>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is { } cached)
            return cached;
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } again)
                return again;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            var loaded = new Dictionary<string, AuthConfig>(StringComparer.Ordinal);
            foreach (var row in await db.Registries.AsNoTracking().ToListAsync(cancellationToken))
            {
                if (ToAuth(row) is { } auth)
                    loaded[row.Registry] = auth;
            }
            return _cache = loaded;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private AuthConfig? ToAuth(RegistryCredential row)
    {
        try
        {
            return new AuthConfig { Username = row.Username, Password = _protector.Unprotect(row.ProtectedPassword), ServerAddress = RegistryAuth.ServerAddress(row.Registry) };
        }
        catch (CryptographicException ex)
        {
            // The keys in /data/keys were lost or replaced; the credential has to be entered again.
            logger.LogWarning(ex, "Can't decrypt the stored credentials for {Registry}", row.Registry);
            return null;
        }
    }

    private RegistryInfo Info(RegistryCredential row) =>
        new(row.Id, row.Name, row.Registry, row.Username, row.UpdatedAt, ToAuth(row) is not null);

    /// <summary>host or host:port, lower case (normalised first).</summary>
    [GeneratedRegex(@"^(?=.{1,255}$)[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?$")]
    private static partial Regex HostPattern();
}
