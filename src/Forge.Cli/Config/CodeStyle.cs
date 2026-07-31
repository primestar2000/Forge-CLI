using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Cli.Config;

/// <summary>
/// How generated code must be written so it agrees with the project it lands in. Detected once
/// at startup; getting this wrong means every generated file arrives with warnings and a
/// formatting diff, which kills the "minimal, reviewable diff" promise on arrival.
/// </summary>
public sealed record CodeStyle(
    bool FileScopedNamespaces,
    bool NullableEnabled,
    bool ImplicitUsings,
    string IndentUnit,
    string NewLine)
{
    public static CodeStyle Default { get; } = new(
        FileScopedNamespaces: true,
        NullableEnabled: true,
        ImplicitUsings: true,
        IndentUnit: "    ",
        NewLine: Environment.NewLine);

    /// <summary>
    /// Reads &lt;Nullable&gt; and &lt;ImplicitUsings&gt; from the target csproj, and infers
    /// namespace style plus indentation by sampling an existing source file near where the new
    /// file will land. Sampling real neighbours beats guessing.
    /// </summary>
    public static CodeStyle Detect(string projectDirectory)
    {
        var nullable = true;
        var implicitUsings = true;

        var csproj = Directory.Exists(projectDirectory)
            ? Directory.GetFiles(projectDirectory, "*.csproj").FirstOrDefault()
            : null;

        if (csproj is not null)
        {
            var text = File.ReadAllText(csproj);
            nullable = ReadFlag(text, "Nullable", "enable", defaultValue: true);
            implicitUsings = ReadFlag(text, "ImplicitUsings", "enable", defaultValue: true);
        }

        var (fileScoped, indent, newLine) = SampleSource(projectDirectory);

        return new CodeStyle(fileScoped, nullable, implicitUsings, indent, newLine);
    }

    private static bool ReadFlag(string csprojText, string element, string trueValue, bool defaultValue)
    {
        var open = $"<{element}>";
        var close = $"</{element}>";
        var start = csprojText.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return defaultValue;

        start += open.Length;
        var end = csprojText.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return defaultValue;

        return csprojText[start..end].Trim().Equals(trueValue, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool FileScoped, string Indent, string NewLine) SampleSource(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
            return (true, "    ", Environment.NewLine);

        // Sample a handful and take the majority — one oddly-formatted file shouldn't decide.
        var samples = Fs.Files(projectDirectory, "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Take(20)
            .ToList();

        if (samples.Count == 0) return (true, "    ", Environment.NewLine);

        int fileScoped = 0, blockScoped = 0, tabs = 0, spaces = 0, crlf = 0, lf = 0;

        foreach (var file in samples)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }

            if (text.Contains("\r\n")) crlf++; else lf++;

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            if (root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any()) fileScoped++;
            else if (root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().Any()) blockScoped++;

            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("\t", StringComparison.Ordinal)) { tabs++; break; }
                if (line.StartsWith("    ", StringComparison.Ordinal)) { spaces++; break; }
            }
        }

        return (
            fileScoped >= blockScoped,
            tabs > spaces ? "\t" : "    ",
            crlf >= lf ? "\r\n" : "\n");
    }
}
