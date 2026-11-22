using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Stacks;

/// <summary>Thrown when a stack can't be saved; the message is for people.</summary>
public sealed class StackRejectedException(string message) : Exception(message);

/// <summary>
/// Stacks Sockless Port manages: a row per stack in the database, and its files on disk.
/// Singleton; each call uses its own database scope.
/// </summary>
public sealed class StackStore(IServiceScopeFactory scopes, IOptions<PortOptions> options, TimeProvider time)
{
    public StackFiles Files { get; } = new(options.Value.StacksPath);

    public async Task<IReadOnlyList<StackDefinition>> ListAsync(int endpointId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.Stacks.AsNoTracking().Where(s => s.EndpointId == endpointId).OrderBy(s => s.Name).ToListAsync(cancellationToken);
    }

    public async Task<StackDefinition?> GetAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        return await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
    }

    /// <exception cref="StackRejectedException">The name is invalid or taken.</exception>
    public async Task<StackDefinition> CreateAsync(int endpointId, string name, StackContent content, bool adopted, CancellationToken cancellationToken = default)
    {
        if (StackNames.Problem(name) is { } problem)
            throw new StackRejectedException(problem);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        if (await db.Stacks.AnyAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken))
            throw new StackRejectedException($"There's already a stack named {name} on this endpoint.");

        var now = time.GetUtcNow();
        var stack = new StackDefinition { EndpointId = endpointId, Name = name, Adopted = adopted, CreatedAt = now, UpdatedAt = now };
        // Files first: a row without files would be a stack that can't be opened.
        await Files.WriteAsync(endpointId, name, content, cancellationToken);
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(cancellationToken);
        return stack;
    }

    /// <returns>False if the stack doesn't exist.</returns>
    public async Task<bool> SaveAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return false;
        await Files.WriteAsync(endpointId, name, content, cancellationToken);
        stack.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Records a successful deploy of <paramref name="content"/>.</summary>
    public async Task MarkDeployedAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return;
        await Files.WriteDeployedAsync(endpointId, name, content, cancellationToken);
        stack.DeployedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>After a <c>down</c>: nothing is deployed any more.</summary>
    public async Task MarkDownAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return;
        Files.DeleteDeployed(endpointId, name);
        stack.DeployedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Forgets the stack and deletes its files. Its containers are left alone.</summary>
    /// <returns>False if the stack doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int endpointId, string name, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.EndpointId == endpointId && s.Name == name, cancellationToken);
        if (stack is null)
            return false;
        db.Stacks.Remove(stack);
        await db.SaveChangesAsync(cancellationToken);
        Files.Delete(endpointId, name);
        return true;
    }
}
