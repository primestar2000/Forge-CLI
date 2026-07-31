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

    public string TemplateName => templateName;

    /// <summary>Marker file recording which forge version the published stubs came from.</summary>
    public const string VersionMarker = ".forge-version";

    /// <summary>Pristine copies of the built-ins as they were at publish time — the diff baseline.</summary>
    public const string BaselineFolder = ".baseline";

    public bool IsPublished(string stubName) => File.Exists(OverridePath(stubName));

    public string OverridePath(string stubName) =>
        Path.Combine(solutionRoot, stubOverridesPath, ToRelativePath(stubName));

    public string BaselinePath(string stubName) =>
        Path.Combine(solutionRoot, stubOverridesPath, BaselineFolder, ToRelativePath(stubName));

    public string VersionMarkerPath =>
        Path.Combine(solutionRoot, stubOverridesPath, VersionMarker);

    public string StubDirectory => Path.Combine(solutionRoot, stubOverridesPath);

    public bool HasBaseline(string stubName) => File.Exists(BaselinePath(stubName));

    private static string ToRelativePath(string stubName) =>
        stubName.Replace('/', Path.DirectorySeparatorChar);

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
        var resource = ResourcePrefix + stubName.Replace('\\', '/');
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new StubException(
                $"Built-in stub '{stubName}' not found (looked for embedded resource '{resource}'). " +
                $"This is a bug in forge.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string ResourcePrefix => $"Forge.Cli.Stubs/{templateName}/Snippets/";

    /// <summary>All built-in stub names, with '/' preserved for nested stubs.</summary>
    public IEnumerable<string> BuiltInStubNames()
    {
        var prefix = ResourcePrefix;
        return Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>Published stub names, discovered by walking .forge/stubs (excluding the baseline).</summary>
    public IEnumerable<string> PublishedStubNames()
    {
        if (!Directory.Exists(StubDirectory)) return [];

        var baseline = Path.Combine(StubDirectory, BaselineFolder);

        return Directory
            .EnumerateFiles(StubDirectory, "*.txt", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(baseline, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(StubDirectory, f).Replace('\\', '/'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Substitutes {{Token}} placeholders, then parses the result to prove it is valid C#.
    /// Catching a broken stub here rather than in the user's next build is the difference
    /// between a clear message and a confusing compiler error in generated code.
    /// </summary>
    /// <summary>Prefix marking a line as forge stub documentation rather than generated output.</summary>
    public const string DirectivePrefix = "// forge:";

    /// <summary>
    /// Strips "// forge:" directive lines. They document the stub for whoever edits it and must
    /// not reach generated output — but Load() keeps them, so stub:publish still hands teams the
    /// documentation.
    ///
    /// The prefix is deliberately forge-specific rather than "any leading comment": a licence
    /// header is the most common stub customisation there is, and stripping every leading
    /// comment silently ate it.
    /// </summary>
    internal static string StripTokenHeader(string template)
    {
        var lines = template.Replace("\r\n", "\n").Split('\n');

        var kept = lines
            .Where(l => !l.TrimStart().StartsWith(DirectivePrefix, StringComparison.Ordinal))
            .ToList();

        return kept.Count == lines.Length ? template : string.Join("\n", kept);
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
