using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sockless.Port.Templates;

/// <summary>A logo ready to serve.</summary>
/// <param name="MediaType">One of <see cref="TemplateLogos"/>' image types, read from the bytes, not the server's header.</param>
public sealed record TemplateLogo(byte[] Bytes, string MediaType);

/// <summary>
/// Templates' logos, downloaded by the server and served from its own origin, because the page's
/// content security policy allows images from nowhere else. Kept on disk in
/// <c>{DataPath}/templates/logos</c> (not backed up: they're downloaded again), fetched at most
/// four at a time, and a logo that failed isn't tried again for a day.
/// </summary>
public sealed class TemplateLogos(TemplateHttp http, IOptions<PortOptions> options, TimeProvider time, ILogger<TemplateLogos> logger)
{
    public const int MaxBytes = 512 * 1024;

    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _downloads = new(4);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);

    private string Folder => Path.Combine(options.Value.DataPath, "templates", "logos");

    /// <returns>Null when there's none: not an image, too large, or it can't be downloaded.</returns>
    public async Task<TemplateLogo?> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return FromDataUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var file = Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))));
        if (File.Exists(file))
            return Identify(await File.ReadAllBytesAsync(file, cancellationToken));
        if (_failed.TryGetValue(uri.AbsoluteUri, out var failedAt) && time.GetUtcNow() - failedAt < RetryAfter)
            return null;

        await _downloads.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(file))
                return Identify(await File.ReadAllBytesAsync(file, cancellationToken));
            var (bytes, _) = await http.GetImageAsync(uri, cancellationToken);
            if (Identify(bytes) is not { } logo)
            {
                _failed[uri.AbsoluteUri] = time.GetUtcNow();
                return null;
            }
            Directory.CreateDirectory(Folder);
            var temporary = file + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, file, overwrite: true);
            return logo;
        }
        catch (TemplateDownloadException ex)
        {
            logger.LogDebug("No logo from {Url}: {Reason}", uri, ex.Message);
            _failed[uri.AbsoluteUri] = time.GetUtcNow();
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Deletes every downloaded logo, e.g. after a source is removed.</summary>
    public void Clear()
    {
        _failed.Clear();
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    private static TemplateLogo? FromDataUrl(string url)
    {
        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(url[(comma + 1)..]);
            return bytes.Length <= MaxBytes ? Identify(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The image type from the file's first bytes; null for anything else.</summary>
    internal static TemplateLogo? Identify(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        string? type = b switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
            [0, 0, 1, 0, ..] => "image/x-icon",
            _ when LooksLikeSvg(b) => "image/svg+xml",
            _ => null,
        };
        return type is null || bytes.Length > MaxBytes ? null : new TemplateLogo(bytes, type);
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]).TrimStart('﻿').TrimStart();
        return (head.StartsWith('<')) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
