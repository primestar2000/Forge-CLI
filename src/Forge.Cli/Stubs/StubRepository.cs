using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Stubs;

public sealed class StubException(string message) : Exception(message);

/// <summary>
/// Resolves stub templates: a team's published override in .forge/stubs/ always wins over the
/// built-in embedded default. This is what lets the CLI stay generic while each team's
/// conventions live in version control in their own repo.
/// </summary>
public sealed class StubRepository(string solutionRoot, string stubOverridesPath, string templateName)
{
    private static readonly Assembly Assembly = typeof(StubRepository).Assembly;

    public bool IsPublished(string stubName) => File.Exists(OverridePath(stubName));

    private string OverridePath(string stubName) =>
        Path.Combine(solutionRoot, stubOverridesPath, stubName);

    public string Load(string stubName)
    {
        var overridePath = OverridePath(stubName);
        if (File.Exists(overridePath)) return File.ReadAllText(overridePath);
        return LoadBuiltIn(stubName);
    }

    /// <summary>
    /// The embedded default, ignoring any published override. Used by stub:diff and the doctor
    /// drift check to compare a team's customisation against upstream.
    /// </summary>
    public string LoadBuiltIn(string stubName)
    {
        // Stub names may be nested ("Solution/Program.cs.txt"). Embedded-resource names are
        // dot-separated, so the directory separator has to be translated for the lookup.
        // The published-override path keeps the slashes, so .forge/stubs mirrors the layout.
        var resource = $"Forge.Cli.Templates.{templateName}.Snippets.{stubName.Replace('/', '.').Replace('\\', '.')}";
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new StubException(
                $"Built-in stub '{stubName}' not found (looked for embedded resource '{resource}'). " +
                $"This is a bug in forge.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>All built-in stub names, used by stub:publish.</summary>
    public IEnumerable<string> BuiltInStubNames()
    {
        var prefix = $"Forge.Cli.Templates.{templateName}.Snippets.";
        return Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>
    /// Substitutes {{Token}} placeholders, then parses the result to prove it is valid C#.
    /// Catching a broken stub here rather than in the user's next build is the difference
    /// between a clear message and a confusing compiler error in generated code.
    /// </summary>
    /// <summary>
    /// Strips the leading "// Tokens: ..." header. That block documents the stub for whoever
    /// edits it and must not reach generated output — but Load() keeps it, so stub:publish
    /// still hands teams the documentation.
    /// </summary>
    internal static string StripTokenHeader(string template)
    {
        var lines = template.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].TrimStart().StartsWith("// Tokens:", StringComparison.Ordinal))
            return template;

        var skip = 1;
        while (skip < lines.Length && lines[skip].TrimStart().StartsWith("//", StringComparison.Ordinal))
            skip++;

        return string.Join("\n", lines.Skip(skip));
    }

    /// <summary>
    /// Substitutes tokens without parsing the result as C#. For .sln, .csproj and JSON stubs,
    /// where a C# parse would obviously fail.
    /// </summary>
    public string RenderRaw(string stubName, IReadOnlyDictionary<string, string> model) =>
        Substitute(stubName, StripTokenHeader(Load(stubName)), model, IsPublished(stubName));

    private string Substitute(
        string stubName,
        string template,
        IReadOnlyDictionary<string, string> model,
        bool published)
    {
        var output = new StringBuilder();

        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0) { output.Append(template, index, template.Length - index); break; }

            var close = template.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0) { output.Append(template, index, template.Length - index); break; }

            output.Append(template, index, open - index);

            var token = template[(open + 2)..close].Trim();
            if (!model.TryGetValue(token, out var value))
            {
                throw new StubException(
                    $"Stub '{stubName}' references unknown token '{{{{{token}}}}}'." + Environment.NewLine +
                    (published
                        ? $"  -> This stub is a published override in {stubOverridesPath}/ — check your edits."
                        : "  -> This is a built-in stub; please file a bug.") + Environment.NewLine +
                    $"  Known tokens: {string.Join(", ", model.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
            }

            output.Append(value);
            index = close + 2;
        }

        return output.ToString();
    }

    public string Render(string stubName, IReadOnlyDictionary<string, string> model)
    {
        var published = IsPublished(stubName);
        var rendered = Substitute(stubName, StripTokenHeader(Load(stubName)), model, published);

        var diagnostics = CSharpSyntaxTree.ParseText(rendered)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (diagnostics.Count > 0)
        {
            throw new StubException(
                $"Stub '{stubName}' produced invalid C#: {diagnostics[0].GetMessage()} " +
                $"at {diagnostics[0].Location.GetLineSpan().StartLinePosition}." + Environment.NewLine +
                (published
                    ? $"  -> This stub is a published override in {stubOverridesPath}/ — check your edits."
                    : "  -> This is a built-in stub; please file a bug."));
        }

        return rendered;
    }
}
