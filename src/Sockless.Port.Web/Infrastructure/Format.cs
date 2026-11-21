using System.Globalization;

namespace Sockless.Port.Web.Infrastructure;

public static class Format
{
    private static readonly string[] DecimalUnits = ["B", "kB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>Decimal units, as <c>docker system df</c> reports sizes: <c>1.2 GB</c>.</summary>
    public static string Bytes(long bytes) => Scale(bytes, 1000, DecimalUnits);

    /// <summary>Binary units, as memory is reported: <c>15.6 GiB</c>.</summary>
    public static string BinaryBytes(long bytes) => Scale(bytes, 1024, BinaryUnits);

    private static string Scale(long bytes, double step, string[] units)
    {
        if (bytes <= 0)
            return "0 B";

        var value = (double)bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>UTC until the time-zone preference arrives (Phase 9).</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
