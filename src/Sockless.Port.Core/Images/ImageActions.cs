using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Images;

/// <param name="Untagged">Tags removed.</param>
/// <param name="Deleted">Image and layer IDs deleted from disk.</param>
public sealed record ImageRemoval(IReadOnlyList<string> Untagged, IReadOnlyList<string> Deleted);

public static class ImageActions
{
    /// <summary>
    /// Removes an image with all its tags. Without <paramref name="force"/>, an image with several
    /// tags is untagged one tag at a time (removing by ID would need force), and the engine still
    /// refuses an image that containers use. With it, the image goes even if stopped containers use it.
    /// </summary>
    public static async Task<ImageRemoval> RemoveAsync(DockerClient client, string id, bool force, CancellationToken cancellationToken = default)
    {
        var (image, _) = await client.InspectImageAsync(id, cancellationToken);
        var tags = image.RepoTags?.Where(t => t != "<none>:<none>").ToList() ?? [];

        var items = new List<ImageDeleteItem>();
        if (!force && tags.Count > 1)
        {
            foreach (var tag in tags)
                items.AddRange(await client.RemoveImageAsync(tag, cancellationToken: cancellationToken));
        }
        else
        {
            items.AddRange(await client.RemoveImageAsync(image.Id, force, cancellationToken: cancellationToken));
        }
        return Summarize(items);
    }

    public static ImageRemoval Summarize(IEnumerable<ImageDeleteItem>? items)
    {
        var list = items?.ToList() ?? [];
        // Docker's prune also "untags" dangling images by their ID; only names are tags.
        return new ImageRemoval(
            [.. list.Select(i => i.Untagged).OfType<string>().Where(t => !t.StartsWith("sha256:", StringComparison.Ordinal))],
            [.. list.Select(i => i.Deleted).OfType<string>()]);
    }

    /// <summary>The 12-character short ID people know from <c>docker images</c>.</summary>
    public static string ShortId(string id) =>
        (id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id) is var hex && hex.Length > 12 ? hex[..12] : hex;
}
