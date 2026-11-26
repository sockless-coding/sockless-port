using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Registries;

namespace Sockless.Port.Volumes;

/// <summary>
/// Reads a volume's files through a short-lived helper container that mounts it read-only and
/// has no network. <see cref="OpenAsync"/> starts the helper (a change on the engine, so it's
/// for POSTs); reads only use an open one. A helper is removed once idle, and its own command
/// ends after <see cref="HelperLifetime"/>, so one left behind by a crash stops by itself.
/// The only write is <see cref="SaveTextAsync"/> (from a POST), through a writable helper of its own.
/// </summary>
public sealed class VolumeBrowser : IAsyncDisposable
{
    public const string HelperKind = "volume-browser";
    public const string VolumeLabel = "sockless-port.volume";
    public const int MaxEntries = 2000;

    /// <summary>Where the helper mounts the volume.</summary>
    internal const string MountPath = "/volume";

    /// <summary>How long a helper's own <c>sleep</c> runs.</summary>
    internal static readonly TimeSpan HelperLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a helper is reused; well inside <see cref="HelperLifetime"/>, so a command never meets a stopped one.</summary>
    internal static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(25);

    /// <summary>How long a save's writable helper's own <c>sleep</c> runs; it's removed as soon as the save ends.</summary>
    internal static readonly TimeSpan WriterLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Unused this long, a helper is removed.</summary>
    internal static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(int Endpoint, string Volume), Helper> _helpers = new();
    private readonly ConcurrentDictionary<(int Endpoint, string Volume), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<int, bool> _swept = new();
    private readonly RegistryCredentials _registries;
    private readonly TimeProvider _time;
    private readonly ILogger<VolumeBrowser> _logger;
    private readonly ITimer _sweeper;

    public VolumeBrowser(IOptions<PortOptions> options, RegistryCredentials registries, TimeProvider time, ILogger<VolumeBrowser> logger)
    {
        HelperImage = options.Value.HelperImage;
        _registries = registries;
        _time = time;
        _logger = logger;
        _sweeper = time.CreateTimer(_ => _ = SweepIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string HelperImage { get; }

    private sealed class Helper(EngineConnection connection, string containerId, DateTimeOffset started)
    {
        public EngineConnection Connection { get; } = connection;
        public string ContainerId { get; } = containerId;
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset LastUsed { get; set; } = started;
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Starts a helper for the volume, or keeps using the open one. Pulls the helper image if needed.</summary>
    public async Task OpenAsync(EngineConnection connection, DockerClient client, string volume, CancellationToken cancellationToken = default) =>
        await HelperAsync(connection, client, volume, create: true, cancellationToken);

    /// <summary>Whether reads of the volume can use an open helper.</summary>
    public bool IsOpen(int endpointId, string volume) =>
        _helpers.TryGetValue((endpointId, volume), out var helper) && _time.GetUtcNow() - helper.Started < ReuseFor;

    // ---------------------------------------------------------------- reading

    /// <summary>Lists a directory, or describes a file or link when <paramref name="path"/> isn't one.</summary>
    /// <param name="path">A path from <see cref="VolumePath.Normalize"/>.</param>
    public async Task<VolumeListing> ListAsync(EngineConnection connection, DockerClient client, string volume, string path, CancellationToken cancellationToken = default)
    {
        var target = MountPath + (path == "/" ? "" : path);
        // -maxdepth 1 without -mindepth: find prints the start point first, which says whether it's a directory.
        // Links aren't followed (no -L), so a link can't lead outside the volume.
        string[] command = ["find", target, "-maxdepth", "1", "-exec", "stat", "-c", StatOutput.Format, "{}", "+"];
        var result = await RunAsync(connection, client, volume, command, cancellationToken);

        var entries = StatOutput.Parse(result.Stdout, MountPath).ToList();
        var self = entries.FirstOrDefault(e => e.Path == path);
        if (self is null)
        {
            throw new VolumeBrowseException(result.Stderr.Contains("No such file", StringComparison.Ordinal)
                ? $"{path} doesn't exist in {volume}."
                : $"Couldn't read {path}: {FirstLine(result.Stderr) ?? "the helper printed nothing."}");
        }
        entries.Remove(self);

        var listed = entries
            .OrderBy(e => e.Kind != VolumeEntryKind.Directory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var truncated = result.Truncated || listed.Count > MaxEntries;
        return new VolumeListing(path, self, self.Kind == VolumeEntryKind.Directory ? listed.Take(MaxEntries).ToList() : [], truncated,
            result.ExitCode is not 0 && !result.Truncated ? FirstLine(result.Stderr) : null);
    }

    /// <summary>
    /// Hands a regular file's content to <paramref name="write"/> as a stream. Only regular files:
    /// a link is refused, since Podman would send what it points to.
    /// </summary>
    public async Task DownloadFileAsync(EngineConnection connection, DockerClient client, string volume, string path,
        Func<VolumeEntry, Stream, Task> write, CancellationToken cancellationToken = default)
    {
        var listing = await ListAsync(connection, client, volume, path, cancellationToken);
        if (listing.Self.Kind != VolumeEntryKind.File)
            throw new VolumeBrowseException($"{path} isn't a regular file, so it can't be downloaded as one.");

        var helper = await HelperAsync(connection, client, volume, create: false, cancellationToken);
        await using var archive = await client.GetArchiveAsync(helper, MountPath + path, cancellationToken);
        await using var tar = new TarReader(archive);
        var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken);
        if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, DataStream: { } data })
            throw new VolumeBrowseException($"{path} changed while it was being read. Try again.");
        await write(listing.Self with { Size = entry.Length }, data);
    }

    /// <summary>Hands a directory (or file) to <paramref name="write"/> as an uncompressed tar stream, as <c>docker cp</c> would.</summary>
    public async Task DownloadArchiveAsync(EngineConnection connection, DockerClient client, string volume, string path,
        Func<Stream, Task> write, CancellationToken cancellationToken = default)
    {
        var helper = await HelperAsync(connection, client, volume, create: false, cancellationToken);
        await using var archive = await client.GetArchiveAsync(helper, MountPath + path, cancellationToken);
        await write(archive);
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Reads a regular file for the editor, through the open helper.</summary>
    /// <param name="entry">The file, from <see cref="ListAsync"/>.</param>
    public async Task<VolumeTextFile> ReadTextAsync(EngineConnection connection, DockerClient client, string volume, VolumeEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind != VolumeEntryKind.File)
            throw new VolumeBrowseException($"{entry.Path} isn't a regular file, so it can't be edited.");
        if (entry.Size > VolumeText.MaxBytes)
            return new VolumeTextFile(entry, null, null, VolumeText.TooLarge);

        var helper = await HelperAsync(connection, client, volume, create: false, cancellationToken);
        var bytes = await ReadBytesAsync(client, helper, entry.Path, cancellationToken);
        var (text, notEditable) = VolumeText.Decode(bytes);
        return new VolumeTextFile(entry with { Size = bytes.Length }, text, text is null ? null : VolumeText.Version(bytes), notEditable);
    }

    /// <summary>
    /// Replaces a regular file's content with <paramref name="text"/>, keeping its owner, mode,
    /// byte order mark and line endings. Browsing stays read-only: the write goes through a helper
    /// of its own that mounts the volume writable for this save only, and is removed after it.
    /// </summary>
    /// <param name="version">The <see cref="VolumeTextFile.Version"/> the text was edited from; null overwrites whatever is there.</param>
    /// <returns>The version of the saved file.</returns>
    /// <exception cref="VolumeFileChangedException">The file isn't at <paramref name="version"/> any more.</exception>
    public async Task<string> SaveTextAsync(EngineConnection connection, DockerClient client, string volume, string path, string text, string? version,
        CancellationToken cancellationToken = default)
    {
        if (path == "/")
            throw new VolumeBrowseException("The volume's root is a folder, not a file.");

        // The volume's lock: two saves can't interleave their check and write, and a release
        // (before removing the volume) waits for the save to finish.
        var gate = _locks.GetOrAdd((connection.Id, volume), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureImageAsync(client, cancellationToken);
            var helper = await StartHelperAsync(client, volume, readOnly: false, WriterLifetime, cancellationToken);
            try
            {
                var target = MountPath + path;
                // No -L: a link is described, never followed (it could point outside the volume).
                var stat = await client.RunExecAsync(helper, ["stat", "-c", "%F|%u|%g|%a", target], maxOutputBytes: 64 * 1024,
                    timeout: TimeSpan.FromSeconds(30), cancellationToken);
                var fields = stat.Stdout.Trim().Split('|');
                if (stat.ExitCode is not 0 || fields.Length != 4)
                {
                    throw new VolumeBrowseException(stat.Stderr.Contains("No such file", StringComparison.Ordinal)
                        ? $"{path} doesn't exist in {volume} any more."
                        : $"Couldn't read {path}: {FirstLine(stat.Stderr) ?? "the helper printed nothing."}");
                }
                if (fields[0] is not ("regular file" or "regular empty file")
                    || !int.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture, out var uid)
                    || !int.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out var gid)
                    || fields[3] is not { Length: > 0 and <= 4 } octal || !octal.All(c => c is >= '0' and <= '7'))
                    throw new VolumeBrowseException($"{path} isn't a regular file any more, so it wasn't saved.");
                var mode = (UnixFileMode)Convert.ToInt32(fields[3], 8);

                var current = await ReadBytesAsync(client, helper, path, cancellationToken);
                if (version is not null && VolumeText.Version(current) != version)
                    throw new VolumeFileChangedException(path);
                var bytes = VolumeText.Encode(text, current);
                if (bytes.Length > VolumeText.MaxBytes)
                    throw new VolumeBrowseException($"The edited file is larger than {VolumeText.MaxBytes / 1024 / 1024} MiB, so it wasn't saved.");

                using var tar = new MemoryStream();
                await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, VolumePath.Name(path))
                    {
                        Mode = mode,
                        Uid = uid,
                        Gid = gid,
                        ModificationTime = _time.GetUtcNow(),
                        DataStream = new MemoryStream(bytes),
                    }, cancellationToken);
                }
                tar.Position = 0;
                var parent = VolumePath.Parent(path)!;
                await client.PutArchiveAsync(helper, MountPath + (parent == "/" ? "" : parent), tar, cancellationToken);
                _logger.LogInformation("Saved {Path} in volume {Volume} on endpoint {Endpoint}", path, volume, connection.Id);
                return VolumeText.Version(bytes);
            }
            finally
            {
                await RemoveAsync(client, helper);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A regular file's bytes, up to one more than <see cref="VolumeText.MaxBytes"/> (enough to tell it's too large).</summary>
    private static async Task<byte[]> ReadBytesAsync(DockerClient client, string helper, string path, CancellationToken cancellationToken)
    {
        await using var archive = await client.GetArchiveAsync(helper, MountPath + path, cancellationToken);
        await using var tar = new TarReader(archive);
        var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken);
        if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile })
            throw new VolumeBrowseException($"{path} changed while it was being read. Try again.");
        // An empty file's entry has no data stream.
        if (entry.DataStream is not { } data || entry.Length == 0)
            return [];
        var buffer = new byte[Math.Min(entry.Length, VolumeText.MaxBytes + 1)];
        var read = await data.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Removes the helper of a volume, so the volume can be removed.</summary>
    public async Task ReleaseAsync(EngineConnection connection, DockerClient client, string volume, CancellationToken cancellationToken = default)
    {
        var key = (connection.Id, volume);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_helpers.TryRemove(key, out var helper))
                await RemoveAsync(client, helper.ContainerId);
            // Also ones another instance (or an earlier run) left for this volume.
            foreach (var leftover in await client.ListContainersAsync(HelperFilter(volume), cancellationToken: cancellationToken))
                await RemoveAsync(client, leftover.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes every helper on the endpoint, so a prune sees the volumes as unused.</summary>
    public async Task ReleaseAllAsync(EngineConnection connection, DockerClient client, CancellationToken cancellationToken = default)
    {
        foreach (var key in _helpers.Keys.Where(k => k.Endpoint == connection.Id).ToList())
            await ReleaseAsync(connection, client, key.Volume, cancellationToken);
        foreach (var leftover in await client.ListContainersAsync(HelperFilter(null), cancellationToken: cancellationToken))
            await RemoveAsync(client, leftover.Id);
    }

    private async Task<ExecResult> RunAsync(EngineConnection connection, DockerClient client, string volume, string[] command, CancellationToken cancellationToken)
    {
        var helper = await HelperAsync(connection, client, volume, create: false, cancellationToken);
        try
        {
            return await client.RunExecAsync(helper, command, maxOutputBytes: 1024 * 1024, timeout: TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (DockerApiException ex) when (ex.IsNotFound || ex.IsConflict)
        {
            // Removed or stopped behind our back (another instance's cleanup, an engine restart).
            _helpers.TryRemove((connection.Id, volume), out _);
            throw new VolumeBrowserClosedException();
        }
    }

    /// <summary>The helper container for a volume: reused while fresh, otherwise (with <paramref name="create"/>) created and started.</summary>
    /// <exception cref="VolumeBrowserClosedException">No fresh helper, and <paramref name="create"/> is false.</exception>
    private async Task<string> HelperAsync(EngineConnection connection, DockerClient client, string volume, bool create, CancellationToken cancellationToken)
    {
        var key = (connection.Id, volume);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_helpers.TryGetValue(key, out var existing))
            {
                if (now - existing.Started < ReuseFor)
                {
                    existing.LastUsed = now;
                    return existing.ContainerId;
                }
                _helpers.TryRemove(key, out _);
                await RemoveAsync(client, existing.ContainerId);
            }
            if (!create)
                throw new VolumeBrowserClosedException();

            await SweepExitedAsync(connection, client, cancellationToken);
            await EnsureImageAsync(client, cancellationToken);
            var id = await StartHelperAsync(client, volume, readOnly: true, HelperLifetime, cancellationToken);
            _helpers[key] = new Helper(connection, id, now);
            _logger.LogDebug("Started volume browser helper {Container} for {Volume} on endpoint {Endpoint}", id[..12], volume, connection.Id);
            return id;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>How long a copy's helpers' own <c>sleep</c> runs; they're removed as soon as the copy ends.</summary>
    internal static readonly TimeSpan CopyLifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// A helper for copying a volume to another endpoint (a move, from a POST): read-only on the
    /// source, writable on the target, mounted at <see cref="MountPath"/>, with no network. Not
    /// tracked or reused like the browser's own: the caller removes it with <see cref="RemoveCopyHelperAsync"/>.
    /// </summary>
    internal async Task<string> StartCopyHelperAsync(DockerClient client, string volume, bool readOnly, CancellationToken cancellationToken)
    {
        await EnsureImageAsync(client, cancellationToken);
        return await StartHelperAsync(client, volume, readOnly, CopyLifetime, cancellationToken);
    }

    internal Task RemoveCopyHelperAsync(DockerClient client, string containerId) => RemoveAsync(client, containerId);

    /// <summary>Creates and starts a helper that mounts the volume, has no network and ends after <paramref name="lifetime"/>.</summary>
    private async Task<string> StartHelperAsync(DockerClient client, string volume, bool readOnly, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var name = $"sockless-port-{(readOnly ? "files" : "write")}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var body = new JsonObject
        {
            ["Image"] = HelperImage,
            ["Cmd"] = new JsonArray("sleep", ((int)lifetime.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["Labels"] = new JsonObject { [ContainerLabels.Helper] = HelperKind, [VolumeLabel] = volume },
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "none",
                ["Mounts"] = new JsonArray(new JsonObject
                {
                    ["Type"] = "volume",
                    ["Source"] = volume,
                    ["Target"] = MountPath,
                    ["ReadOnly"] = readOnly,
                }),
            },
        };
        var created = await client.CreateContainerAsync(name, body, cancellationToken);
        try
        {
            await client.StartContainerAsync(created.Id, cancellationToken);
        }
        catch
        {
            await RemoveAsync(client, created.Id);
            throw;
        }
        return created.Id;
    }

    private async Task EnsureImageAsync(DockerClient client, CancellationToken cancellationToken)
    {
        if (await client.ImageExistsAsync(HelperImage, cancellationToken))
            return;
        try
        {
            var auth = await _registries.AuthHeaderForAsync(HelperImage, cancellationToken);
            await foreach (var _ in client.PullImageAsync(HelperImage, auth, cancellationToken)) { }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
        {
            throw new VolumeBrowseException(
                $"Reading a volume's files needs the helper image {HelperImage}, and pulling it failed: {EngineErrors.Sentence(ex.Message)} " +
                "Pull it on the engine, or set Port:HelperImage to an image there that has find and stat.");
        }
    }

    /// <summary>Once per endpoint and run: removes helpers that already stopped (left by a crash or another instance).</summary>
    private async Task SweepExitedAsync(EngineConnection connection, DockerClient client, CancellationToken cancellationToken)
    {
        if (!_swept.TryAdd(connection.Id, true))
            return;
        var filters = new Dictionary<string, string[]>(HelperFilter(null)) { ["status"] = ["exited", "created"] };
        foreach (var leftover in await client.ListContainersAsync(filters, cancellationToken: cancellationToken))
            await RemoveAsync(client, leftover.Id);
    }

    private async Task SweepIdleAsync()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, helper) in _helpers)
        {
            if (now - helper.LastUsed < IdleAfter || !_locks.TryGetValue(key, out var gate) || !await gate.WaitAsync(0))
                continue;
            try
            {
                if (_helpers.TryGetValue(key, out var current) && current == helper && now - helper.LastUsed >= IdleAfter)
                {
                    _helpers.TryRemove(key, out _);
                    if (helper.Connection.Client is { } client)
                        await RemoveAsync(client, helper.ContainerId);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task RemoveAsync(DockerClient client, string containerId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.RemoveContainerAsync(containerId, force: true, cancellationToken: timeout.Token);
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // It stops by itself when its sleep ends, and is swept on the next start.
            _logger.LogWarning("Couldn't remove volume browser helper {Container}: {Message}", containerId[..Math.Min(12, containerId.Length)], ex.Message);
        }
    }

    private static Dictionary<string, string[]> HelperFilter(string? volume) => new()
    {
        ["label"] = volume is null ? [$"{ContainerLabels.Helper}={HelperKind}"] : [$"{ContainerLabels.Helper}={HelperKind}", $"{VolumeLabel}={volume}"],
    };

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync();
        foreach (var (_, helper) in _helpers)
        {
            if (helper.Connection.Client is { } client)
                await RemoveAsync(client, helper.ContainerId);
        }
        _helpers.Clear();
    }
}
