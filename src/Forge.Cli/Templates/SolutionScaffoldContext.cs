using Forge.Cli.Config;
using Forge.Cli.Stubs;

namespace Forge.Cli.Templates;

/// <summary>
/// Context for make:solution. Unlike TemplateContext there is no config yet — the solution is
/// being created — so code style is the template's own convention rather than something detected.
/// </summary>
public sealed class SolutionScaffoldContext(StubRepository stubs, string root, bool force)
{
    public StubRepository Stubs { get; } = stubs;
    public string Root { get; } = root;
    public bool Force { get; } = force;

    /// <summary>New solutions get modern defaults: file-scoped namespaces, nullable, implicit usings.</summary>
    public CodeStyle CodeStyle { get; } = CodeStyle.Default;

    public string PathIn(params string[] segments) =>
        Path.GetFullPath(Path.Combine(new[] { Root }.Concat(segments).ToArray()));

    public string Render(string stubName, IReadOnlyDictionary<string, string> model) =>
        CodeStyleFormatter.Apply(Stubs.Render(stubName, model), CodeStyle);

    public string RenderRaw(string stubName, IReadOnlyDictionary<string, string> model) =>
        Stubs.RenderRaw(stubName, model);
}
