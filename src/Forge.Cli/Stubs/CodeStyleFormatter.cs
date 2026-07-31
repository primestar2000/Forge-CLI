using System.Text;
using Forge.Cli.Config;

namespace Forge.Cli.Stubs;

/// <summary>
/// Adapts freshly generated stub output to the target project's conventions.
///
/// Stubs are always authored with file-scoped namespaces; if the project uses block-scoped,
/// the generated text is converted here. Keeping one stub per artifact rather than one per
/// namespace style halves the maintenance surface and removes a whole class of drift.
///
/// Note this operates ONLY on text forge just generated from its own stubs — never on user
/// source, which is always edited through <see cref="Roslyn.SyntaxPatcher"/>.
/// </summary>
public static class CodeStyleFormatter
{
    public static string Apply(string generated, CodeStyle style)
    {
        var text = style.FileScopedNamespaces ? generated : ToBlockScoped(generated, style.IndentUnit);
        return NormalizeNewLines(text, style.NewLine);
    }

    private static string ToBlockScoped(string source, string indentUnit)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n').ToList();

        var nsIndex = lines.FindIndex(l =>
            l.TrimStart().StartsWith("namespace ", StringComparison.Ordinal) &&
            l.TrimEnd().EndsWith(";", StringComparison.Ordinal));

        if (nsIndex < 0) return source;

        var declaration = lines[nsIndex].TrimEnd();
        lines[nsIndex] = declaration[..^1].TrimEnd();
        lines.Insert(nsIndex + 1, "{");

        for (var i = nsIndex + 2; i < lines.Count; i++)
            if (lines[i].Length > 0)
                lines[i] = indentUnit + lines[i];

        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add("}");

        return string.Join("\n", lines) + "\n";
    }

    private static string NormalizeNewLines(string text, string newLine)
    {
        var normalized = text.Replace("\r\n", "\n");
        return newLine == "\n" ? normalized : normalized.Replace("\n", newLine);
    }

    /// <summary>Emits a using block only when the project does not have ImplicitUsings on.</summary>
    public static string UsingsFor(CodeStyle style, params string[] namespaces)
    {
        if (style.ImplicitUsings || namespaces.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var ns in namespaces) sb.Append("using ").Append(ns).Append(';').Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }
}
