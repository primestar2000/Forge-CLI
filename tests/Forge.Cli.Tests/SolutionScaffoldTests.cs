using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Tests;

/// <summary>
/// Plan-level tests for make:solution. The slow "does the output actually compile" check lives
/// in the Compile-category gate; these run in milliseconds and guard the shape.
/// </summary>
public class SolutionScaffoldTests
{
    private static (SolutionScaffoldContext Ctx, string Root) Context()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-sol-" + Guid.NewGuid().ToString("N")[..8]);
        var stubs = new StubRepository(root, ".forge/stubs", "OnionWolverineErrorOr");
        return (new SolutionScaffoldContext(stubs, root, force: false), root);
    }

    private static SolutionSpec Spec(string roleGuard = "single-array", bool pinForge = false) =>
        new("OfficeCommute", ".", roleGuard, "wolverine", "net8.0", "UserRole", null, pinForge);

    private static GenerationPlan Plan(string roleGuard = "single-array")
    {
        var (ctx, _) = Context();
        var result = new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec(roleGuard), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(result.Ok, result.Error);
        return result.Plan;
    }

    /// <summary>Content of the single planned file whose name ends with <paramref name="endsWith"/>.</summary>
    private static string ContentOf(GenerationPlan plan, string endsWith) =>
        plan.Actions.OfType<FileAction.Create>()
            .Single(a => a.Path.EndsWith(endsWith, StringComparison.Ordinal))
            .Content;

    [Fact]
    public void Scaffolds_projects_solution_config_and_tool_manifest()
    {
        var files = Plan().Actions.Select(a => Path.GetFileName(a.Path)).ToList();

        Assert.Contains("OfficeCommute.sln", files);
        Assert.Contains("OfficeCommute.Domain.csproj", files);
        Assert.Contains("OfficeCommute.ApplicationService.csproj", files);
        Assert.Contains("OfficeCommute.Infrastructure.csproj", files);
        Assert.Contains("OfficeCommute.API.csproj", files);
        Assert.Contains("forge.config.json", files);
    }

    /// <summary>
    /// --with-runtime must wire tier 2 COMPLETELY. This template is Wolverine-based, so there is
    /// no scenario where someone opts into the runtime and does not want invoke:*. Emitting only
    /// the core package left three manual steps between --with-runtime and a working invoke:list,
    /// which is what a real project hit.
    /// </summary>
    [Fact]
    public async Task With_runtime_wires_tier_two_completely()
    {
        var (ctx, _) = Context();
        var result = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec() with { WithRuntime = true }, CancellationToken.None);

        Assert.True(result.Ok, result.Error);

        var csproj = ContentOf(result.Plan, "OfficeCommute.API.csproj");
        Assert.Contains("Pitechy.Forge.Runtime\"", csproj);
        Assert.Contains("Pitechy.Forge.Runtime.Wolverine\"", csproj);

        var program = ContentOf(result.Plan, "Program.cs");
        Assert.Contains("using Forge.Runtime;", program);
        Assert.Contains("using Forge.Runtime.Wolverine;", program);
        Assert.Contains("builder.Services.AddForgeWolverine();", program);
        Assert.Contains("RunForgeRuntimeAsync(args)", program);
        Assert.Contains("IsForgeInvocation(args)", program);
    }

    /// <summary>Tier 2 is opt-in: without the flag nothing forge-specific may appear.</summary>
    [Fact]
    public async Task Without_runtime_the_scaffold_has_no_forge_dependency()
    {
        var (ctx, _) = Context();
        var result = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec(), CancellationToken.None);

        Assert.DoesNotContain("Pitechy.Forge", ContentOf(result.Plan, "OfficeCommute.API.csproj"));

        var program = ContentOf(result.Plan, "Program.cs");
        Assert.DoesNotContain("Forge.Runtime", program);
        Assert.DoesNotContain("AddForgeWolverine", program);

        // Scoped is the only correct lifetime for a real web request; the singleton swap exists
        // solely for a forge invocation and must not leak into a solution without the runtime.
        Assert.Contains("AddScoped<CurrentUser>()", program);
        Assert.DoesNotContain("AddSingleton<CurrentUser>()", program);
    }

    /// <summary>
    /// A tool manifest takes precedence over a global install, so writing one for a version that
    /// is not restorable makes the very next `dotnet forge` command fail with
    /// "Run dotnet tool restore" — bricking forge in the solution it just created.
    /// It must therefore be opt-in.
    /// </summary>
    [Fact]
    public async Task Tool_manifest_is_opt_in()
    {
        var (ctx, _) = Context();

        var without = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec(), CancellationToken.None);
        Assert.DoesNotContain("dotnet-tools.json", without.Plan.Actions.Select(a => Path.GetFileName(a.Path)));

        var (ctx2, _) = Context();
        var with = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx2, Spec(pinForge: true), CancellationToken.None);
        Assert.Contains("dotnet-tools.json", with.Plan.Actions.Select(a => Path.GetFileName(a.Path)));
    }

    [Fact]
    public void Role_guard_style_selects_the_matching_marker_interface()
    {
        Assert.Contains("IRequireExplicitRoles.cs",
            Plan("single-array").Actions.Select(a => Path.GetFileName(a.Path)));

        Assert.Contains("IRequiresExplicitRoles.cs",
            Plan("role-and-subrole").Actions.Select(a => Path.GetFileName(a.Path)));
    }

    /// <summary>
    /// Project GUIDs are derived from the project name, never Guid.NewGuid(): re-running
    /// make:solution must produce byte-identical output or the command is not idempotent.
    /// </summary>
    [Fact]
    public void Output_is_deterministic_across_runs()
    {
        var first = Plan();
        var second = Plan();

        var firstContent = first.Actions.OfType<FileAction.Create>()
            .Where(a => a.Path.EndsWith(".sln", StringComparison.Ordinal))
            .Select(a => a.Content).Single();

        var secondContent = second.Actions.OfType<FileAction.Create>()
            .Where(a => a.Path.EndsWith(".sln", StringComparison.Ordinal))
            .Select(a => a.Content).Single();

        Assert.Equal(firstContent, secondContent);
        Assert.Contains(SolutionSpec.ProjectGuid("OfficeCommute.Domain"), firstContent);
    }

    [Fact]
    public async Task Rejects_an_invalid_solution_name()
    {
        var (ctx, _) = Context();
        var result = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec() with { Name = "9Bad" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(Forge.Cli.Cli.ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task Planning_writes_nothing_to_disk()
    {
        var (ctx, root) = Context();
        _ = await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanSolution(ctx, Spec(), CancellationToken.None);

        Assert.False(Directory.Exists(root), "Planning must never touch the filesystem.");
    }
}
