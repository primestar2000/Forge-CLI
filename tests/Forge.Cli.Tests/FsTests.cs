using Forge.Cli.Config;

namespace Forge.Cli.Tests;

/// <summary>
/// Regression tests for two bugs found by running doctor on a real machine.
/// </summary>
public class FsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-fs-" + Guid.NewGuid().ToString("N")[..8]);

    public FsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The upward walk must stop at a .git directory. Without this bound, running forge in a
    /// scratch directory found a stray forge.config.json in the user's HOME folder and treated
    /// the home directory as a solution root — which would scaffold source files into it.
    /// </summary>
    [Fact]
    public void FindFileUpward_stops_at_the_repository_boundary()
    {
        // outer/forge.config.json   <- must NOT be found
        // outer/repo/.git
        // outer/repo/src           <- search starts here
        var outer = Path.Combine(_root, "outer");
        var repo = Path.Combine(outer, "repo");
        var src = Path.Combine(repo, "src");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(outer, "forge.config.json"), "{}");

        Assert.Null(Fs.FindFileUpward(src, "forge.config.json"));
    }

    [Fact]
    public void FindFileUpward_finds_a_config_inside_the_repository()
    {
        var repo = Path.Combine(_root, "repo");
        var src = Path.Combine(repo, "src", "App");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var expected = Path.Combine(repo, "forge.config.json");
        File.WriteAllText(expected, "{}");

        Assert.Equal(expected, Fs.FindFileUpward(src, "forge.config.json"));
    }

    /// <summary>
    /// Recursive enumeration must not throw on inaccessible directories. Windows user profiles
    /// contain legacy junctions ("Application Data") that deny access to everyone; an unguarded
    /// EnumerateFiles crashed doctor outright.
    /// </summary>
    [Fact]
    public void Files_survives_a_missing_or_inaccessible_root()
    {
        Assert.Empty(Fs.Files(Path.Combine(_root, "does-not-exist"), "*.cs"));

        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllText(Path.Combine(_root, "real", "A.cs"), "class A {}");

        Assert.Single(Fs.Files(_root, "*.cs"));
    }
}
