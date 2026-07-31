using Forge.Cli.Config;
using Forge.Cli.Stubs;

namespace Forge.Cli.Diagnostics;

/// <summary>
/// What checks get to look at. Config may be null — doctor must still run and report useful
/// environment diagnostics in a directory that has never been initialised.
/// </summary>
public sealed class DoctorContext(ForgeConfig? config, string solutionRoot)
{
    private SourceIndex? _index;

    public ForgeConfig? Config { get; } = config;
    public string SolutionRoot { get; } = solutionRoot;
    public bool HasConfig => Config is not null;

    /// <summary>Built lazily — a full syntactic scan is wasted work if config is missing.</summary>
    public SourceIndex Index => _index ??= SourceIndex.Build(SolutionRoot);

    public StubRepository? Stubs { get; set; }

    public string PathIn(params string[] segments) =>
        Path.GetFullPath(Path.Combine(new[] { SolutionRoot }.Concat(segments).ToArray()));

    /// <summary>Concatenated text of every csproj, for package-reference probing.</summary>
    public string AllProjectFiles => _projects ??= string.Join("\n", Fs.Files(SolutionRoot, "*.csproj")
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        .Select(p => { try { return File.ReadAllText(p); } catch { return string.Empty; } }));

    private string? _projects;
}

public interface IDoctorCheck
{
    IEnumerable<CheckResult> Run(DoctorContext context);
}
