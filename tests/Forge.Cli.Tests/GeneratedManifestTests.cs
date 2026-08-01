using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Tests;

/// <summary>
/// The manifest is what turns --force from "hope you committed first" into something safe to run.
/// </summary>
public class GeneratedManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-man-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _file;

    public GeneratedManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        _file = Path.Combine(_root, "src", "Order.cs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private TemplateContext Context(bool force, bool overwriteModified, GeneratedManifest manifest) =>
        new(new ForgeConfig { SolutionName = "App" }, _root, CodeStyle.Default,
            new StubRepository(_root, ".forge/stubs", "OnionWolverineErrorOr"), force)
        {
            Manifest = manifest,
            OverwriteModified = overwriteModified
        };

    [Fact]
    public void Hash_ignores_line_ending_differences()
    {
        // CRLF/LF churn from git or an editor must not read as "the developer edited this".
        Assert.Equal(
            GeneratedManifest.ComputeHash("class A\n{\n}\n"),
            GeneratedManifest.ComputeHash("class A\r\n{\r\n}\r\n"));
    }

    [Fact]
    public void Applying_a_plan_records_created_files_and_round_trips()
    {
        var manifest = GeneratedManifest.Load(_root);
        var plan = GenerationPlan.Of(new FileAction.Create(_file, "class Order { }"));

        Assert.True(new PlanExecutor().Apply(plan, manifest, "make:entity").Ok);

        var reloaded = GeneratedManifest.Load(_root);
        var entry = reloaded.Find(_file);

        Assert.NotNull(entry);
        Assert.Equal("make:entity", entry!.GeneratedBy);
        Assert.False(reloaded.IsModifiedSinceGeneration(_file));
    }

    [Fact]
    public void An_edited_file_is_detected_as_modified()
    {
        var manifest = GeneratedManifest.Load(_root);
        new PlanExecutor().Apply(GenerationPlan.Of(new FileAction.Create(_file, "class Order { }")), manifest, "make:entity");

        File.WriteAllText(_file, "class Order { public bool IsHighValue() => true; }");

        Assert.True(GeneratedManifest.Load(_root).IsModifiedSinceGeneration(_file));
    }

    /// <summary>Unknown provenance is treated as modified — never clobber what forge cannot prove it wrote.</summary>
    [Fact]
    public void A_file_forge_never_generated_counts_as_modified()
    {
        File.WriteAllText(_file, "class Order { }");
        Assert.True(GeneratedManifest.Load(_root).IsModifiedSinceGeneration(_file));
    }

    [Fact]
    public void Force_overwrites_untouched_generated_output()
    {
        var manifest = GeneratedManifest.Load(_root);
        new PlanExecutor().Apply(GenerationPlan.Of(new FileAction.Create(_file, "class Order { }")), manifest, "make:entity");

        var plan = Context(force: true, overwriteModified: false, GeneratedManifest.Load(_root))
            .CreateOrSkip(_file, () => "class Order { int X; }");

        Assert.IsType<FileAction.Create>(plan.Actions.Single());
    }

    /// <summary>
    /// The whole point: --force alone must not destroy work someone did inside generated output.
    /// </summary>
    [Fact]
    public void Force_alone_refuses_to_overwrite_an_edited_file()
    {
        var manifest = GeneratedManifest.Load(_root);
        new PlanExecutor().Apply(GenerationPlan.Of(new FileAction.Create(_file, "class Order { }")), manifest, "make:entity");
        File.WriteAllText(_file, "class Order { public bool IsHighValue() => true; }");

        var plan = Context(force: true, overwriteModified: false, GeneratedManifest.Load(_root))
            .CreateOrSkip(_file, () => "class Order { }");

        var skip = Assert.IsType<FileAction.Skip>(plan.Actions.Single());
        Assert.True(skip.Protected, "A refused overwrite must be flagged so it surfaces as exit code 4.");
        Assert.Contains("--overwrite-modified", skip.Reason);
        Assert.True(plan.HasProtectedSkips);
    }

    [Fact]
    public void Overwrite_modified_is_the_explicit_second_key()
    {
        var manifest = GeneratedManifest.Load(_root);
        new PlanExecutor().Apply(GenerationPlan.Of(new FileAction.Create(_file, "class Order { }")), manifest, "make:entity");
        File.WriteAllText(_file, "class Order { public bool IsHighValue() => true; }");

        var plan = Context(force: true, overwriteModified: true, GeneratedManifest.Load(_root))
            .CreateOrSkip(_file, () => "class Order { }");

        Assert.IsType<FileAction.Create>(plan.Actions.Single());
    }

    [Fact]
    public void Without_force_an_existing_file_is_an_ordinary_skip()
    {
        File.WriteAllText(_file, "class Order { }");

        var plan = Context(force: false, overwriteModified: false, GeneratedManifest.Load(_root))
            .CreateOrSkip(_file, () => "class Order { }");

        var skip = Assert.IsType<FileAction.Skip>(plan.Actions.Single());
        Assert.False(skip.Protected);
        Assert.Contains("--force", skip.Reason);
    }

    /// <summary>A corrupt manifest must degrade to conservative behaviour, never block generation.</summary>
    [Fact]
    public void A_corrupt_manifest_is_survivable()
    {
        var path = Path.Combine(_root, ".forge");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "manifest.json"), "{ this is not json");

        var manifest = GeneratedManifest.Load(_root);

        Assert.True(manifest.IsEmpty);
        File.WriteAllText(_file, "class Order { }");
        Assert.True(manifest.IsModifiedSinceGeneration(_file));
    }
}
