namespace Sockless.Port.Stacks;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

/// <param name="OldLine">1-based line in the old text; null for added lines.</param>
/// <param name="NewLine">1-based line in the new text; null for removed lines.</param>
public sealed record DiffLine(DiffKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>A run of changes with a few unchanged lines around it.</summary>
public sealed record DiffHunk(IReadOnlyList<DiffLine> Lines)
{
    public int Added => Lines.Count(l => l.Kind == DiffKind.Added);

    public int Removed => Lines.Count(l => l.Kind == DiffKind.Removed);
}

/// <summary>Line diff of two small texts (compose files), for review before a redeploy.</summary>
public static class TextDiff
{
    /// <summary>Above this many cells the middle is shown as replaced rather than diffed.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Lines(string? oldText, string? newText)
    {
        var a = Split(oldText);
        var b = Split(newText);

        // Unchanged head and tail first: most edits touch a few lines in the middle.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
            suffix++;

        var result = new List<DiffLine>(Math.Max(a.Length, b.Length) + 8);
        for (var i = 0; i < prefix; i++)
            result.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, a[i]));

        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if ((long)n * m > MaxCells)
        {
            for (var i = 0; i < n; i++)
                result.Add(new DiffLine(DiffKind.Removed, prefix + i + 1, null, a[prefix + i]));
            for (var j = 0; j < m; j++)
                result.Add(new DiffLine(DiffKind.Added, null, prefix + j + 1, b[prefix + j]));
        }
        else
        {
            Middle(a, b, prefix, n, m, result);
        }

        for (var k = 0; k < suffix; k++)
        {
            var i = a.Length - suffix + k;
            var j = b.Length - suffix + k;
            result.Add(new DiffLine(DiffKind.Same, i + 1, j + 1, a[i]));
        }
        return result;
    }

    /// <summary>Changed lines grouped with <paramref name="context"/> unchanged lines on each side.</summary>
    public static IReadOnlyList<DiffHunk> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind == DiffKind.Same)
            {
                i++;
                continue;
            }
            var start = Math.Max(0, i - context);
            var end = i;
            // Extend while the next change is within 2 × context unchanged lines.
            while (end < lines.Count)
            {
                if (lines[end].Kind != DiffKind.Same)
                {
                    end++;
                    continue;
                }
                var next = end;
                while (next < lines.Count && lines[next].Kind == DiffKind.Same)
                    next++;
                if (next < lines.Count && next - end <= 2 * context)
                    end = next;
                else
                    break;
            }
            var stop = Math.Min(lines.Count, end + context);
            hunks.Add(new DiffHunk([.. lines.Skip(start).Take(stop - start)]));
            i = stop;
        }
        return hunks;
    }

    private static void Middle(string[] a, string[] b, int offset, int n, int m, List<DiffLine> result)
    {
        // Classic LCS table over the changed middle; small inputs only (see MaxCells).
        var table = new int[(n + 1) * (m + 1)];
        int At(int i, int j) => table[i * (m + 1) + j];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i * (m + 1) + j] = a[offset + i] == b[offset + j]
                    ? At(i + 1, j + 1) + 1
                    : Math.Max(At(i + 1, j), At(i, j + 1));
            }
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[offset + x] == b[offset + y])
            {
                result.Add(new DiffLine(DiffKind.Same, offset + x + 1, offset + y + 1, a[offset + x]));
                x++;
                y++;
            }
            else if (y < m && (x == n || At(x, y + 1) >= At(x + 1, y)))
            {
                // Removals before additions on a tie read better: "- old" then "+ new".
                if (x < n && At(x + 1, y) == At(x, y + 1))
                {
                    result.Add(new DiffLine(DiffKind.Removed, offset + x + 1, null, a[offset + x]));
                    x++;
                }
                else
                {
                    result.Add(new DiffLine(DiffKind.Added, null, offset + y + 1, b[offset + y]));
                    y++;
                }
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Removed, offset + x + 1, null, a[offset + x]));
                x++;
            }
        }
    }

    private static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // A final newline doesn't make an extra empty line.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
