using Forge.Cli.Config;
using Forge.Cli.Stubs;

namespace Forge.Cli.Templates;

/// <summary>
/// Everything a generator needs, resolved once. Parameters that belong to the whole run live
/// here rather than being threaded through every ITemplate method signature.
/// </summary>
public sealed class TemplateContext(
    ForgeConfig config,
    string solutionRoot,
    CodeStyle codeStyle,
    StubRepository stubs,
    bool force)
{
    public ForgeConfig Config { get; } = config;
    public string SolutionRoot { get; } = solutionRoot;
    public CodeStyle CodeStyle { get; } = codeStyle;
    public StubRepository Stubs { get; } = stubs;
    public bool Force { get; } = force;

    /// <summary>Absolute path from a solution-root-relative path.</summary>
    public string PathIn(params string[] segments) =>
        Path.GetFullPath(Path.Combine(new[] { SolutionRoot }.Concat(segments).ToArray()));

    /// <summary>Path relative to the solution root, with forward slashes — the form used in output.</summary>
    public string Relative(string absolutePath) =>
        Path.GetRelativePath(SolutionRoot, absolutePath).Replace('\\', '/');

    /// <summary>Renders a stub and adapts it to the target project's code style.</summary>
    public string Render(string stubName, IReadOnlyDictionary<string, string> model) =>
        CodeStyleFormatter.Apply(Stubs.Render(stubName, model), CodeStyle);

    public string NullableMarker => CodeStyle.NullableEnabled ? "?" : string.Empty;

    public string Usings(params string[] namespaces) =>
        CodeStyleFormatter.UsingsFor(CodeStyle, namespaces);
}
