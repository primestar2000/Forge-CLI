using Forge.Cli.Config;
using Forge.Cli.Diagnostics;
using Forge.Cli.Diagnostics.Checks;

namespace Forge.Cli.Tests;

/// <summary>
/// doctor's tier-2 reporting. A diagnostic that overstates what works is worse than none — it
/// sends people looking in the wrong place. Every case here was observed on a real project.
/// </summary>
public class RuntimePackageCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-tier-" + Guid.NewGuid().ToString("N")[..8]);

    public RuntimePackageCheckTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "src", "App.API"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string[] packages, string program)
    {
        var references = string.Join("\n", packages.Select(p => $"    <PackageReference Include=\"{p}\" Version=\"1.0.0\" />"));
        File.WriteAllText(Path.Combine(_root, "src", "App.API", "App.API.csproj"),
            $"<Project>\n  <ItemGroup>\n{references}\n  </ItemGroup>\n</Project>");
        File.WriteAllText(Path.Combine(_root, "src", "App.API", "Program.cs"), program);
    }

    private IReadOnlyList<CheckResult> Run()
    {
        var config = new ForgeConfig { ApiProject = "src/App.API" };
        return new RuntimePackageCheck().Run(new DoctorContext(config, _root)).ToList();
    }

    private static CheckResult For(IReadOnlyList<CheckResult> results, string label) =>
        results.Single(r => r.Label == label);

    private const string FullyWired = """
        builder.Services.AddForgeWolverine();
        var app = builder.Build();
        if (await app.RunForgeRuntimeAsync(args)) return;
        """;

    [Fact]
    public void Reports_both_capabilities_available_when_fully_wired()
    {
        Write(["Pitechy.Forge.Runtime", "Pitechy.Forge.Runtime.Wolverine"], FullyWired);

        var results = Run();
        Assert.Equal(CheckStatus.Pass, For(results, "db:seed").Status);
        Assert.Equal(CheckStatus.Pass, For(results, "invoke:*").Status);
    }

    /// <summary>
    /// The exact state a real project was in: core package present, satellite missing. doctor
    /// used to announce "Tier 2 - all commands available" and invoke:list refused seconds later.
    /// </summary>
    [Fact]
    public void Core_package_alone_does_not_make_invoke_available()
    {
        Write(["Pitechy.Forge.Runtime"], FullyWired);

        var results = Run();
        Assert.Equal(CheckStatus.Pass, For(results, "db:seed").Status);
        Assert.Equal(CheckStatus.Warn, For(results, "invoke:*").Status);
        Assert.Contains("Wolverine", For(results, "invoke:*").Detail);
    }

    /// <summary>
    /// The package id was matched as a substring, so the satellite alone satisfied the check for
    /// the core package — which it does not provide.
    /// </summary>
    [Fact]
    public void The_satellite_does_not_satisfy_the_core_package_check()
    {
        Write(["Pitechy.Forge.Runtime.Wolverine"], FullyWired);

        Assert.Equal(CheckStatus.Warn, For(Run(), "db:seed").Status);
    }

    /// <summary>
    /// Adding the package by hand without wiring Program.cs leaves a state where forge launches
    /// the app and it simply never reports a result. doctor must see that.
    /// </summary>
    [Fact]
    public void A_referenced_package_without_the_hook_is_not_available()
    {
        Write(["Pitechy.Forge.Runtime", "Pitechy.Forge.Runtime.Wolverine"],
            "var app = builder.Build();\napp.Run();");

        var seed = For(Run(), "db:seed");
        Assert.Equal(CheckStatus.Warn, seed.Status);
        Assert.Contains("RunForgeRuntimeAsync", seed.Detail);
        Assert.Contains("forge runtime:install", seed.Fix!);
    }

    [Fact]
    public void The_satellite_without_AddForgeWolverine_is_not_available()
    {
        Write(["Pitechy.Forge.Runtime", "Pitechy.Forge.Runtime.Wolverine"],
            "var app = builder.Build();\nif (await app.RunForgeRuntimeAsync(args)) return;");

        var invoke = For(Run(), "invoke:*");
        Assert.Equal(CheckStatus.Warn, invoke.Status);
        Assert.Contains("AddForgeWolverine", invoke.Detail);
        Assert.Contains("forge runtime:install", invoke.Fix!);
    }

    [Fact]
    public void Neither_package_reports_both_unavailable_with_the_install_command()
    {
        Write([], "var app = builder.Build();\napp.Run();");

        var results = Run();
        Assert.Equal(CheckStatus.Warn, For(results, "db:seed").Status);
        Assert.Equal(CheckStatus.Warn, For(results, "invoke:*").Status);

        // One command, not three hand edits — the whole point of runtime:install.
        Assert.Contains("forge runtime:install", For(results, "db:seed").Fix!);
        Assert.Contains("forge runtime:install", For(results, "invoke:*").Fix!);
    }
}
