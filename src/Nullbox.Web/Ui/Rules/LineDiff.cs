namespace Nullbox.Web.Ui.Rules;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>A line diff (longest common subsequence) for comparing rule-set versions' JSON.</summary>
public static class LineDiff
{
    /// <summary>Beyond this many lines on either side the diff is only "removed everything, added everything".</summary>
    public const int MaxLines = 4000;

    public static IReadOnlyList<DiffLine> Compute(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = Split(before);
        var b = Split(after);
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            return [.. a.Select(l => new DiffLine(DiffKind.Removed, l)), .. b.Select(l => new DiffLine(DiffKind.Added, l))];
        }

        // lengths[i, j]: LCS length of a[i..] and b[j..].
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = a[i] == b[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(Math.Max(a.Length, b.Length));
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        result.AddRange(a[x..].Select(l => new DiffLine(DiffKind.Removed, l)));
        result.AddRange(b[y..].Select(l => new DiffLine(DiffKind.Added, l)));
        return result;
    }

    private static string[] Split(string text) => text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');
}
