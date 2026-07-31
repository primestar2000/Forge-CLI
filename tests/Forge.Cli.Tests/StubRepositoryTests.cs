using Forge.Cli.Stubs;

namespace Forge.Cli.Tests;

public class StubRepositoryTests
{
    private static StubRepository Repo() =>
        new(solutionRoot: Path.GetTempPath(), stubOverridesPath: ".forge/stubs", templateName: "OnionWolverineErrorOr");

    /// <summary>
    /// Guards the embedded-resource naming contract. If MSBuild's resource naming ever changes,
    /// or a stub is added outside the wildcard, every generator breaks at runtime with a
    /// "this is a bug in forge" message — this catches it at build time instead.
    /// </summary>
    [Fact]
    public void Every_builtin_stub_is_embedded_and_loadable()
    {
        var assembly = typeof(StubRepository).Assembly;
        var all = assembly.GetManifestResourceNames();
        var names = Repo().BuiltInStubNames().ToList();

        Assert.True(names.Count > 0,
            $"No stubs resolved.\n" +
            $"  assembly : {assembly.Location}\n" +
            $"  written  : {(File.Exists(assembly.Location) ? File.GetLastWriteTime(assembly.Location).ToString("O") : "n/a")}\n" +
            $"  size     : {(File.Exists(assembly.Location) ? new FileInfo(assembly.Location).Length : 0)}\n" +
            $"  resources: {(all.Length == 0 ? "(none at all)" : string.Join(", ", all))}");

        Assert.Contains("RepositoryInterface.cs.txt", names);
        Assert.Contains("Repository.cs.txt", names);
        Assert.Contains("Errors.cs.txt", names);

        foreach (var name in names)
            Assert.False(string.IsNullOrWhiteSpace(Repo().Load(name)), $"Stub '{name}' loaded empty.");
    }

    [Fact]
    public void Unknown_token_is_a_hard_error_naming_the_stub()
    {
        var ex = Assert.Throws<StubException>(() =>
            Repo().Render("Errors.cs.txt", new Dictionary<string, string> { ["Entity"] = "Gig" }));

        Assert.Contains("Errors.cs.txt", ex.Message);
        Assert.Contains("Known tokens", ex.Message);
    }

    [Fact]
    public void Rendered_stub_is_valid_csharp()
    {
        var rendered = Repo().Render("Errors.cs.txt", new Dictionary<string, string>
        {
            ["Usings"] = "",
            ["Namespace"] = "App.Errors",
            ["Entity"] = "Gig",
            ["EntityCamelSpaced"] = "gig"
        });

        Assert.Contains("public static class Gig", rendered);
        Assert.Contains("namespace App.Errors;", rendered);
    }
}
