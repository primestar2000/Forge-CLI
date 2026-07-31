using System.Text;

namespace Forge.Cli.Planning;

public enum DiffKind { Context, Added, Removed }

public readonly record struct DiffLine(DiffKind Kind, string Text);

/// <summary>
/// Minimal line-based diff (LCS). Used to render --dry-run previews and, in tests, to assert
/// that a Roslyn patch only ever ADDS lines — the check that catches trivia duplication and
/// accidental whole-file reflow, which a "does it still compile" assertion sails straight past.
/// </summary>
public static class LineDiff
{
    public static IReadOnlyList<DiffLine> Compute(string before, string after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);

        // lengths[i, j] = LCS length of a[i..] and b[j..]
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = a[i] == b[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Context, a[x]));
                x++; y++;
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
        while (x < a.Length) result.Add(new DiffLine(DiffKind.Removed, a[x++]));
        while (y < b.Length) result.Add(new DiffLine(DiffKind.Added, b[y++]));

        return result;
    }

    /// <summary>Renders only changed regions plus <paramref name="context"/> surrounding lines.</summary>
    public static string Render(string before, string after, int context = 3)
    {
        var lines = Compute(before, after);
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Context) continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                keep[j] = true;
        }

        var sb = new StringBuilder();
        var skipping = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!keep[i]) { skipping = true; continue; }
            if (skipping) { sb.AppendLine("   ..."); skipping = false; }

            var prefix = lines[i].Kind switch
            {
                DiffKind.Added => " + ",
                DiffKind.Removed => " - ",
                _ => "   "
            };
            sb.AppendLine(prefix + lines[i].Text);
        }
        return sb.ToString();
    }

    public static bool IsAdditionsOnly(string before, string after) =>
        Compute(before, after).All(l => l.Kind != DiffKind.Removed);

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
