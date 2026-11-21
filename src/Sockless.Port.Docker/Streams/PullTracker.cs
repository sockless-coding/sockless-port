using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Streams;

/// <summary>How far a layer has got. Phases only move forward.</summary>
public enum LayerPhase
{
    Waiting,
    Downloading,
    Downloaded,
    Extracting,
    Done,
}

/// <param name="Current">Bytes downloaded so far; equals <paramref name="Total"/> once downloaded.</param>
/// <param name="Total">Layer size in bytes, or 0 when the engine never said (small layers, config blobs).</param>
public sealed record LayerProgress(string Id, LayerPhase Phase, string Status, long Current, long Total);

/// <param name="Messages">Lines that aren't about one layer ("Pulling from library/nginx", "Digest: …", "Status: …").</param>
/// <param name="DownloadedBytes">Bytes downloaded across layers whose size is known.</param>
/// <param name="TotalBytes">Sum of the sizes known so far; grows as layers start downloading.</param>
public sealed record PullSnapshot(IReadOnlyList<LayerProgress> Layers, IReadOnlyList<string> Messages, long DownloadedBytes, long TotalBytes)
{
    public static readonly PullSnapshot Empty = new([], [], 0, 0);

    public int LayersDone => Layers.Count(l => l.Phase >= LayerPhase.Downloaded);
}

/// <summary>
/// Folds the pull progress stream into per-layer state. Engines differ: Docker's containerd
/// store reports extraction in seconds and skips <c>Downloading</c> for small blobs; Podman never
/// sends <c>Pull complete</c> or a final status, and repeats lines. Not thread-safe.
/// </summary>
public sealed class PullTracker
{
    private const int MaxMessages = 20;

    private readonly Dictionary<string, LayerProgress> _layers = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<string> _messages = [];

    public void Apply(PullProgress line)
    {
        var status = line.Status?.Trim();
        if (string.IsNullOrEmpty(status))
            return;

        // Docker's first line carries the tag in "id": "Pulling from library/busybox", id "1.37".
        if (string.IsNullOrEmpty(line.Id) || status.StartsWith("Pulling from ", StringComparison.Ordinal) || status.StartsWith("Pulling repository", StringComparison.Ordinal))
        {
            if (_messages.Count == 0 || _messages[^1] != status)
                _messages.Add(status);
            if (_messages.Count > MaxMessages)
                _messages.RemoveAt(0);
            return;
        }

        if (!_layers.TryGetValue(line.Id, out var layer))
        {
            layer = new LayerProgress(line.Id, LayerPhase.Waiting, status, 0, 0);
            _order.Add(line.Id);
        }

        var phase = PhaseOf(status) ?? layer.Phase;
        if (phase < layer.Phase)
            phase = layer.Phase;

        var (current, total) = (layer.Current, layer.Total);
        // Byte counts only: extraction progress in seconds ("units": "s") isn't a size.
        if (line.ProgressDetail is { Units: null or "" or "B" } detail && phase == LayerPhase.Downloading)
        {
            if (detail.Total > 0)
                total = detail.Total;
            current = Math.Max(current, detail.Current);
        }
        if (phase >= LayerPhase.Downloaded)
            current = total;

        _layers[line.Id] = layer with { Phase = phase, Status = status, Current = current, Total = total };
    }

    /// <summary>The pull finished: every layer is done, whatever the engine last said about it.</summary>
    public void Complete()
    {
        foreach (var id in _order)
        {
            var layer = _layers[id];
            _layers[id] = layer with { Phase = LayerPhase.Done, Current = layer.Total };
        }
    }

    public PullSnapshot Snapshot()
    {
        var layers = _order.Select(id => _layers[id]).ToList();
        return new PullSnapshot(layers, [.. _messages], layers.Sum(l => l.Current), layers.Sum(l => l.Total));
    }

    private static LayerPhase? PhaseOf(string status) => status switch
    {
        "Pulling fs layer" or "Waiting" => LayerPhase.Waiting,
        "Downloading" => LayerPhase.Downloading,
        "Verifying Checksum" or "Download complete" => LayerPhase.Downloaded,
        "Extracting" => LayerPhase.Extracting,
        "Pull complete" or "Already exists" => LayerPhase.Done,
        _ => null,
    };
}
