using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sockless.Port.Swarm;

/// <summary>
/// The units swarm specs use, read and written the way the Docker CLI's flags take them:
/// durations as <c>10s</c> or <c>1m30s</c> (nanoseconds in the spec), sizes as <c>512M</c> or
/// <c>1.5G</c> (bytes), CPUs as <c>0.5</c> (nano-CPUs).
/// </summary>
public static partial class SwarmUnits
{
    private const long NanosPerSecond = 1_000_000_000;

    /// <summary><c>500ms</c>, <c>10s</c>, <c>1m30s</c>, <c>2h</c> → nanoseconds. A bare number is seconds.</summary>
    public static bool TryParseDuration(string? text, out long nanoseconds)
    {
        nanoseconds = 0;
        text = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
            return false;
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var bare))
        {
            nanoseconds = (long)(bare * NanosPerSecond);
            return true;
        }

        var matches = DurationPart().Matches(text);
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)) != text)
            return false;
        decimal total = 0;
        foreach (Match match in matches)
        {
            var value = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            total += value * match.Groups[2].Value switch
            {
                "ns" => 1m,
                "us" or "µs" => 1_000m,
                "ms" => 1_000_000m,
                "s" => NanosPerSecond,
                "m" => 60m * NanosPerSecond,
                "h" => 3600m * NanosPerSecond,
                _ => 0m,
            };
        }
        if (total > long.MaxValue)
            return false;
        nanoseconds = (long)total;
        return true;
    }

    /// <summary>Nanoseconds → <c>1m30s</c>, <c>500ms</c>, <c>0s</c>.</summary>
    public static string FormatDuration(long nanoseconds)
    {
        if (nanoseconds <= 0)
            return "0s";
        if (nanoseconds % 1_000_000 != 0)
            return $"{nanoseconds.ToString(CultureInfo.InvariantCulture)}ns";
        if (nanoseconds < NanosPerSecond)
            return $"{(nanoseconds / 1_000_000).ToString(CultureInfo.InvariantCulture)}ms";

        var builder = new StringBuilder();
        var seconds = nanoseconds / NanosPerSecond;
        var millis = nanoseconds % NanosPerSecond / 1_000_000;
        if (seconds >= 3600)
        {
            builder.Append((seconds / 3600).ToString(CultureInfo.InvariantCulture)).Append('h');
            seconds %= 3600;
        }
        if (seconds >= 60)
        {
            builder.Append((seconds / 60).ToString(CultureInfo.InvariantCulture)).Append('m');
            seconds %= 60;
        }
        if (seconds > 0 || millis > 0 || builder.Length == 0)
        {
            builder.Append(seconds.ToString(CultureInfo.InvariantCulture));
            if (millis > 0)
                builder.Append('.').Append(millis.ToString("000", CultureInfo.InvariantCulture).TrimEnd('0'));
            builder.Append('s');
        }
        return builder.ToString();
    }

    /// <summary><c>512M</c>, <c>1.5G</c>, <c>64MiB</c>, <c>1048576</c> → bytes. Units are binary, as Docker's flags read them.</summary>
    public static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text?.Trim() ?? "");
        if (!match.Success)
            return false;
        var value = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var multiplier = char.ToLowerInvariant(match.Groups[2].Value.FirstOrDefault('b')) switch
        {
            'b' => 1m,
            'k' => 1024m,
            'm' => 1024m * 1024,
            'g' => 1024m * 1024 * 1024,
            't' => 1024m * 1024 * 1024 * 1024,
            _ => 0m,
        };
        var total = value * multiplier;
        if (multiplier == 0 || total > long.MaxValue)
            return false;
        bytes = (long)total;
        return true;
    }

    /// <summary>Bytes → <c>512M</c>, <c>1.5G</c>: what <see cref="TryParseBytes"/> reads back.</summary>
    public static string FormatBytes(long bytes)
    {
        // The largest unit that reads back exactly with at most two decimals; else plain bytes.
        string[] units = ["T", "G", "M", "K"];
        for (var i = 0; i < units.Length; i++)
        {
            var size = (decimal)Math.Pow(1024, units.Length - i);
            var value = bytes / size;
            if (value >= 1 && decimal.Round(value, 2) * size == bytes)
                return $"{decimal.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture)}{units[i]}";
        }
        return bytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>0.5</c>, <c>2</c> → nano-CPUs.</summary>
    public static bool TryParseCpus(string? text, out long nanoCpus)
    {
        nanoCpus = 0;
        if (!decimal.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cpus) || cpus <= 0 || cpus > 100_000)
            return false;
        nanoCpus = (long)(cpus * NanosPerSecond);
        return nanoCpus > 0;
    }

    public static string FormatCpus(long nanoCpus) => (nanoCpus / (decimal)NanosPerSecond).ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ns|us|µs|ms|s|m|h)")]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([kmgt]i?b?|b)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
}
