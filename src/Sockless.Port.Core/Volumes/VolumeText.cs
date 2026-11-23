using System.Security.Cryptography;
using System.Text;

namespace Sockless.Port.Volumes;

/// <summary>
/// Turns a file's bytes into text for the editor and the edited text back into bytes, keeping
/// what a text area can't carry: the byte order mark and the line endings. Browsers send a text
/// area's lines with CRLF whatever the file had, so a save writes the file's own endings again.
/// </summary>
public static class VolumeText
{
    /// <summary>Files larger than this are downloaded, not edited.</summary>
    public const int MaxBytes = 1024 * 1024;

    public static readonly string TooLarge = $"It's larger than {MaxBytes / 1024 / 1024} MiB, so it can only be downloaded.";

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>The text to edit, or why the bytes can't be edited as text.</summary>
    public static (string? Text, string? NotEditable) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
            return (null, TooLarge);
        if (bytes.Contains((byte)0))
            return (null, "It looks like a binary file, so it can only be downloaded.");
        string text;
        try
        {
            text = Strict.GetString(bytes.StartsWith(Bom) ? bytes[Bom.Length..] : bytes);
        }
        catch (DecoderFallbackException)
        {
            return (null, "It isn't UTF-8 text, so it can only be downloaded.");
        }
        // A text area turns a lone CR into a line break, which would change the file.
        for (var i = text.IndexOf('\r'); i >= 0; i = text.IndexOf('\r', i + 1))
        {
            if (i + 1 >= text.Length || text[i + 1] != '\n')
                return (null, "It has carriage returns that don't end lines, which the editor can't keep, so it can only be downloaded.");
        }
        return (text, null);
    }

    /// <summary>
    /// The edited text as bytes, in the shape of <paramref name="original"/>: its byte order mark,
    /// and CRLF line endings when most of its lines ended that way, LF otherwise.
    /// </summary>
    public static byte[] Encode(string text, ReadOnlySpan<byte> original)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (UsesCrlf(original))
            lf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(lf);
        return original.StartsWith(Bom) ? [.. Bom, .. body] : body;
    }

    /// <summary>Names one content of a file, so a save can tell whether it changed since it was read.</summary>
    public static string Version(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool UsesCrlf(ReadOnlySpan<byte> bytes)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != '\n')
                continue;
            if (i > 0 && bytes[i - 1] == '\r')
                crlf++;
            else
                lf++;
        }
        return crlf > lf;
    }
}
