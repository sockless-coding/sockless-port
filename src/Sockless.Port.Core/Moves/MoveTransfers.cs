using System.Globalization;
using System.Text.Json.Nodes;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;

namespace Sockless.Port.Moves;

/// <summary>What a move's checks found, in the order found.</summary>
public sealed class MoveFindings
{
    private readonly List<MoveFinding> _all = [];

    public IReadOnlyList<MoveFinding> All => _all;

    public void Block(string message) => _all.Add(new(MoveSeverity.Blocker, message));

    public void Warn(string message) => _all.Add(new(MoveSeverity.Warning, message));

    public void Note(string message) => _all.Add(new(MoveSeverity.Note, message));
}

/// <summary>What the checks found in the containers that move: their named volumes and images.</summary>
public sealed record MoveContents(IReadOnlyList<MoveVolume> Volumes, IReadOnlyList<string> Images);

/// <summary>
/// The parts of a move that don't depend on what moves (a stack or a container): checking
/// containers against the target, copying a volume's data, and getting an image onto the target.
/// </summary>
public sealed class MoveTransfers(VolumeBrowser volumes, Registries.RegistryCredentials registries)
{
    /// <summary>How often a volume copy says how far it is.</summary>
    internal const long ProgressEvery = 256L * 1024 * 1024;

    /// <summary>
    /// Checks what the containers need from the target's host: bind mounts, devices, host ports
    /// (taken ones block), rootless ports, image architectures; and which named volumes can be copied.
    /// </summary>
    /// <param name="what">What they make up, for the findings: "the stack", "web".</param>
    public async Task<MoveContents> CheckContainersAsync(EngineConnection source, DockerClient sourceClient, EngineConnection target, DockerClient targetClient,
        IReadOnlyList<ContainerSummary> containers, string what, MoveFindings findings, CancellationToken cancellationToken)
    {
        var volumeNames = new SortedSet<string>(StringComparer.Ordinal);
        var binds = new SortedSet<string>(StringComparer.Ordinal);
        var images = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var container in containers)
        {
            if (container.Image is { Length: > 0 } image && !image.StartsWith("sha256:", StringComparison.Ordinal))
                images.Add(image);
            var (inspect, json) = await sourceClient.InspectContainerAsync(container.Id, cancellationToken);
            foreach (var mount in inspect.Mounts ?? [])
            {
                if (mount.Type == "volume" && mount.Name is { Length: > 0 } volume)
                {
                    if (IsAnonymous(volume))
                        findings.Warn($"{container.DisplayName} has an anonymous volume at {mount.Destination}. It isn't copied: the container gets a new, empty one there.");
                    else
                        volumeNames.Add(volume);
                }
                else if (mount.Type == "bind" && mount.Source is { Length: > 0 } path)
                {
                    binds.Add(path);
                }
            }
            var hostConfig = JsonNode.Parse(json)?["HostConfig"];
            if (hostConfig?["Devices"] is JsonArray { Count: > 0 })
                findings.Warn($"{container.DisplayName} uses devices of {source.Name}'s host. They aren't moved: {target.Name}'s host needs the same ones.");
            if (inspect.HostConfig?.NetworkMode == "host")
                findings.Note($"{container.DisplayName} uses the host's network, so it listens on {target.Name}'s host addresses there.");
        }
        foreach (var path in binds)
            findings.Warn($"{path} on {source.Name}'s host is mounted into {what}. Bind mounts aren't copied: the path must exist on {target.Name}'s host.");

        // Host ports: starting fails if the target publishes them already.
        var wanted = containers.SelectMany(c => c.Ports ?? []).Where(p => p.PublicPort > 0)
            .Select(p => (Port: p.PublicPort, Type: p.Type ?? "tcp")).Distinct().ToList();
        if (wanted.Count > 0)
        {
            var taken = (await targetClient.ListContainersAsync(all: false, cancellationToken))
                .SelectMany(c => (c.Ports ?? []).Where(p => p.PublicPort > 0).Select(p => (Port: p.PublicPort, Type: p.Type ?? "tcp", c.DisplayName)))
                .ToList();
            foreach (var (port, type) in wanted)
            {
                if (taken.FirstOrDefault(t => t.Port == port && t.Type == type) is { DisplayName: { } user })
                    findings.Block($"Port {port}/{type} is already published on {target.Name} by {user}. Free it first, or {what} can't start there.");
            }
            if (target.Engine?.Has(EngineCapabilities.Rootless) == true && wanted.Any(w => w.Port < 1024))
                findings.Warn($"{target.Name} runs rootless, which usually can't publish ports below 1024. Starting fails unless its host allows it.");
        }

        // Images: a copied image runs only on the architecture it was built for.
        var targetArchitecture = Architecture(target.Engine?.Architecture);
        foreach (var image in images)
        {
            try
            {
                var (inspect, _) = await sourceClient.InspectImageAsync(image, cancellationToken);
                if (Architecture(inspect.Architecture) is { } built && targetArchitecture is not null && built != targetArchitecture)
                    findings.Warn($"{image} on {source.Name} is built for {built}, and {target.Name} runs {targetArchitecture}. It runs there only if its registry has a {targetArchitecture} variant.");
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
            }
        }

        var found = new List<MoveVolume>();
        foreach (var volume in volumeNames)
            found.Add(new MoveVolume(volume, await VolumeProblemAsync(sourceClient, target, targetClient, volume, cancellationToken)));
        if (found.Count > 0 && found.All(v => !v.Copyable))
            findings.Note($"None of the volumes can be copied: {what} starts on the target with what's there.");
        return new MoveContents(found, [.. images]);
    }

    /// <summary>A volume made for a container rather than by name (64 hex characters, as both engines generate them).</summary>
    internal static bool IsAnonymous(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigitLower);

    /// <summary>Why a volume's data can't be copied to the target, or null when it can.</summary>
    private static async Task<string?> VolumeProblemAsync(DockerClient sourceClient, EngineConnection target, DockerClient targetClient, string name,
        CancellationToken cancellationToken)
    {
        var (volume, _) = await sourceClient.InspectVolumeAsync(name, cancellationToken);
        if (volume.Driver is { Length: > 0 } driver && driver != "local")
            return $"It uses the {driver} driver, which stores the data outside the host: it isn't copied.";
        if (volume.Options?.ContainsKey("device") == true)
            return $"It's bound to {volume.Options["device"]} on the host: it isn't copied, and that path must exist on {target.Name}'s host.";
        try
        {
            await targetClient.InspectVolumeAsync(name, cancellationToken);
            return $"A volume named {name} exists on {target.Name} already: it's used as it is, and nothing is copied into it.";
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the volume on the target as it is on the source (driver, options, labels, so Compose
    /// takes it as its own), then streams its contents from a read-only helper on the source to a
    /// writable one on the target, through the server.
    /// </summary>
    public async Task CopyVolumeAsync(Action<string> log, EngineConnection source, EngineConnection target, string name, CancellationToken cancellationToken)
    {
        var sourceClient = source.Client ?? throw new IOException($"{source.Name} went offline.");
        var targetClient = target.Client ?? throw new IOException($"{target.Name} went offline.");
        var (volume, _) = await sourceClient.InspectVolumeAsync(name, cancellationToken);
        log($"Copying the volume {name}…");
        await targetClient.CreateVolumeAsync(new VolumeCreateRequest
        {
            Name = name,
            Driver = volume.Driver ?? "local",
            DriverOpts = volume.Options,
            Labels = volume.Labels,
        }, cancellationToken);

        string? reader = null, writer = null;
        try
        {
            reader = await volumes.StartCopyHelperAsync(sourceClient, name, readOnly: true, cancellationToken);
            writer = await volumes.StartCopyHelperAsync(targetClient, name, readOnly: false, cancellationToken);
            await using var archive = await sourceClient.GetArchiveAsync(reader, VolumeBrowser.MountPath, cancellationToken);
            var next = ProgressEvery;
            await using var counting = new CountingStream(archive, copied =>
            {
                if (copied < next)
                    return;
                next = copied + ProgressEvery;
                log($"  {Bytes(copied)} of {name} copied…");
            });
            // The archive's entries are "volume/…": unpacked at the root, they land in the target's mount.
            await targetClient.PutArchiveAsync(writer, "/", counting, cancellationToken, Timeout.InfiniteTimeSpan);
            log($"Copied the volume {name} ({Bytes(counting.Count)}).");
        }
        catch (VolumeBrowseException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            if (reader is not null)
                await volumes.RemoveCopyHelperAsync(sourceClient, reader);
            if (writer is not null)
                await volumes.RemoveCopyHelperAsync(targetClient, writer);
        }
    }

    /// <summary>
    /// Makes sure the target has the image: it's there, or it's pulled (with the registry's
    /// credentials), or else it's copied from the source, for images that are in no registry the
    /// target can reach. A failure is only logged: creating or deploying says what's missing.
    /// </summary>
    public async Task EnsureImageAsync(Action<string> log, EngineConnection source, EngineConnection target, string image, CancellationToken cancellationToken)
    {
        var targetClient = target.Client ?? throw new IOException($"{target.Name} went offline.");
        try
        {
            if (await targetClient.ImageExistsAsync(image, cancellationToken))
            {
                log($"{image} is on {target.Name} already.");
                return;
            }
            log($"Pulling {image} on {target.Name}…");
            var auth = await registries.AuthHeaderForAsync(image, cancellationToken);
            await foreach (var _ in targetClient.PullImageAsync(image, auth, cancellationToken)) { }
            log($"Pulled {image}.");
            return;
        }
        catch (Exception ex) when (IsEngineFailure(ex, cancellationToken))
        {
            log($"Couldn't pull {image} on {target.Name}: {EngineErrors.Sentence(ex.Message)} Copying it from {source.Name} instead.");
        }

        var sourceClient = source.Client ?? throw new IOException($"{source.Name} went offline.");
        try
        {
            await using var tar = await sourceClient.ExportImageAsync(image, cancellationToken: cancellationToken);
            await using var counting = new CountingStream(tar, _ => { });
            var loaded = await targetClient.LoadImageAsync(counting, cancellationToken);
            log($"Copied {image} from {source.Name} ({Bytes(counting.Count)}).{(loaded.Count > 0 ? $" {string.Join(" ", loaded)}" : "")}");
        }
        catch (Exception ex) when (IsEngineFailure(ex, cancellationToken))
        {
            log($"Couldn't copy {image} either: {EngineErrors.Sentence(ex.Message)}");
        }
    }

    /// <summary>A failure of an engine or the connection to it, not of the server.</summary>
    internal static bool IsEngineFailure(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is DockerApiException or HttpRequestException or IOException or TimeoutException or TaskCanceledException or ObjectDisposedException
            or InvalidDataException or System.Text.Json.JsonException;

    /// <summary>"amd64" for "x86_64", "arm64" for "aarch64": the names engines and images use differ.</summary>
    internal static string? Architecture(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => null,
        "x86_64" => "amd64",
        "aarch64" => "arm64",
        var other => other,
    };

    /// <summary>"1.2 GB": decimal units, as the engine's own sizes.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
