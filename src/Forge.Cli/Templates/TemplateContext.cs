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
    private SourceIndex? _domainIndex;
    private SourceIndex? _infrastructureIndex;

    public ForgeConfig Config { get; } = config;
    public string SolutionRoot { get; } = solutionRoot;
    public CodeStyle CodeStyle { get; } = codeStyle;
    public StubRepository Stubs { get; } = stubs;
    public bool Force { get; } = force;

    /// <summary>Set by commands that can legitimately run before the entity type exists.</summary>
    public bool AllowMissingEntity { get; set; }

    /// <summary>
    /// Types declared in the domain project, built lazily. Used to verify an entity actually
    /// exists before generating code that references it.
    /// </summary>
    public SourceIndex DomainIndex =>
        _domainIndex ??= SourceIndex.Build(PathIn(Config.DomainProject));

    /// <summary>Types in the infrastructure project — used to locate the DbContext by name.</summary>
    public SourceIndex InfrastructureIndex =>
        _infrastructureIndex ??= SourceIndex.Build(PathIn(Config.InfrastructureProject));

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
