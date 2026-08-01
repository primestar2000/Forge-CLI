using System.Xml.Linq;

namespace Forge.Cli.Tests;

/// <summary>
/// Guards the isolation the whole tier-2 design rests on.
///
/// Forge.Cli and Forge.Runtime live in one repository because their JSON contract needs a test
/// that can see both sides. The cost of that convenience is that a single ProjectReference —
/// added in good faith to "just share the envelope type" — would collapse the process boundary
/// into ordinary coupling, and it would look perfectly reasonable in review. These tests make
/// that invisible invariant a failing build instead.
/// </summary>
public class ArchitectureTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("*.slnx").Length > 0 || directory.GetFiles("*.sln").Length > 0)
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static XDocument Project(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> References(XDocument project, string element) =>
        project.Descendants(element)
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!);

    /// <summary>
    /// forge holds Roslyn and its own dependency graph; the user's app holds theirs. The ONLY
    /// thing that crosses is JSON. A reference in either direction defeats the process boundary.
    /// </summary>
    [Fact]
    public void The_cli_and_the_runtime_never_reference_each_other()
    {
        var cli = Project("src/Forge.Cli/Forge.Cli.csproj");
        var runtime = Project("src/Forge.Runtime/Forge.Runtime.csproj");

        Assert.DoesNotContain(References(cli, "ProjectReference"), r => r.Contains("Forge.Runtime", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(References(cli, "PackageReference"), r => r.Contains("Forge.Runtime", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(References(runtime, "ProjectReference"), r => r.Contains("Forge.Cli", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(References(runtime, "PackageReference"), r => r.Contains("Forge.Cli", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The csproj check can be bypassed by a transitive path; the loaded assembly cannot.</summary>
    [Fact]
    public void The_cli_assembly_does_not_load_the_runtime_assembly()
    {
        var referenced = typeof(Forge.Cli.Cli.ExitCodes).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.DoesNotContain(referenced, name =>
            string.Equals(name, "Forge.Runtime", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Forge.Runtime lands in the USER's restore graph, so every dependency here is a potential
    /// version conflict in their application — the exact problem tier 2 exists to avoid. Anything
    /// framework-specific (Wolverine, EF, Roslyn) belongs in a satellite package such as
    /// Forge.Runtime.Wolverine, never here.
    /// </summary>
    [Fact]
    public void The_runtime_dependency_footprint_stays_minimal()
    {
        string[] allowed =
        [
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.DependencyInjection.Abstractions"
        ];

        var actual = References(Project("src/Forge.Runtime/Forge.Runtime.csproj"), "PackageReference").ToList();

        var unexpected = actual.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(unexpected.Count == 0,
            "Forge.Runtime gained dependencies that ship into every consuming application: " +
            string.Join(", ", unexpected) + Environment.NewLine +
            "Put framework-specific code in a satellite package instead.");
    }

    /// <summary>
    /// Multi-targeting is not cosmetic: this package is referenced by the user's app and must
    /// match whatever they target, rather than forcing them to upgrade.
    /// </summary>
    [Fact]
    public void The_runtime_multi_targets_every_supported_framework()
    {
        var frameworks = Project("src/Forge.Runtime/Forge.Runtime.csproj")
            .Descendants("TargetFrameworks")
            .FirstOrDefault()?.Value ?? string.Empty;

        Assert.Contains("net8.0", frameworks);
        Assert.Contains("net9.0", frameworks);
        Assert.Contains("net10.0", frameworks);
    }

    /// <summary>
    /// The tool must run on every runtime >= 8. Verified empirically during development: without
    /// RollForward a tool targeting an absent framework dies with "You must install or update
    /// .NET", because the default policy crosses minor versions but not majors.
    /// </summary>
    [Fact]
    public void The_cli_targets_net8_and_rolls_forward()
    {
        var cli = Project("src/Forge.Cli/Forge.Cli.csproj");

        Assert.Equal("net8.0", cli.Descendants("TargetFramework").First().Value);
        Assert.Equal("LatestMajor", cli.Descendants("RollForward").First().Value);
    }
}
