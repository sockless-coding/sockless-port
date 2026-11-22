using System.Text;

namespace Sockless.Port.Stacks;

/// <summary>
/// A stack's files on disk: <c>{root}/{endpointId}/{name}/compose.yaml</c> and <c>.env</c>, plus a copy
/// of both under <c>.deployed/</c> from the last successful deploy, which the diff compares against.
/// </summary>
public sealed class StackFiles(string root)
{
    public const string ComposeFile = "compose.yaml";
    public const string EnvFile = ".env";
    private const string DeployedFolder = ".deployed";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; } = root;

    /// <summary>The project directory Compose runs in. Relative paths in the file resolve against it.</summary>
    public string Directory(int endpointId, string name)
    {
        if (StackNames.Problem(name) is not null)
            throw new ArgumentException($"'{name}' isn't a valid stack name.", nameof(name));
        return Path.Combine(Root, endpointId.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
    }

    public string ComposePath(int endpointId, string name) => Path.Combine(Directory(endpointId, name), ComposeFile);

    public string EnvPath(int endpointId, string name) => Path.Combine(Directory(endpointId, name), EnvFile);

    public bool Exists(int endpointId, string name) => File.Exists(ComposePath(endpointId, name));

    /// <summary>The saved files, or null if there are none.</summary>
    public async Task<StackContent?> ReadAsync(int endpointId, string name, CancellationToken cancellationToken = default) =>
        await ReadFromAsync(Directory(endpointId, name), cancellationToken);

    /// <summary>The files as last deployed, or null if they never were.</summary>
    public async Task<StackContent?> ReadDeployedAsync(int endpointId, string name, CancellationToken cancellationToken = default) =>
        await ReadFromAsync(Path.Combine(Directory(endpointId, name), DeployedFolder), cancellationToken);

    public async Task WriteAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default)
    {
        var directory = Directory(endpointId, name);
        System.IO.Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, ComposeFile), content.Compose, cancellationToken);
        await WriteAtomicAsync(Path.Combine(directory, EnvFile), content.Env, cancellationToken);
    }

    /// <summary>Records <paramref name="content"/> as what's deployed.</summary>
    public async Task WriteDeployedAsync(int endpointId, string name, StackContent content, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Directory(endpointId, name), DeployedFolder);
        System.IO.Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, ComposeFile), content.Compose, cancellationToken);
        await WriteAtomicAsync(Path.Combine(directory, EnvFile), content.Env, cancellationToken);
    }

    /// <summary>Forgets what was deployed, after a <c>down</c>.</summary>
    public void DeleteDeployed(int endpointId, string name)
    {
        var directory = Path.Combine(Directory(endpointId, name), DeployedFolder);
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }

    public void Delete(int endpointId, string name)
    {
        var directory = Directory(endpointId, name);
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }

    private static async Task<StackContent?> ReadFromAsync(string directory, CancellationToken cancellationToken)
    {
        var compose = Path.Combine(directory, ComposeFile);
        if (!File.Exists(compose))
            return null;
        var env = Path.Combine(directory, EnvFile);
        return StackContent.Normalize(
            await File.ReadAllTextAsync(compose, Utf8, cancellationToken),
            File.Exists(env) ? await File.ReadAllTextAsync(env, Utf8, cancellationToken) : "");
    }

    // A crash mid-write must not leave half a compose file behind.
    private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, Utf8, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
