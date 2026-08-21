using System.Diagnostics;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;
using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests.Infrastructure;

public sealed record BuildOutcome(int ExitCode, string Output)
{
    public IReadOnlyList<string> Errors => Lines("error ");
    public IReadOnlyList<string> Warnings => Lines("warning ");

    private IReadOnlyList<string> Lines(string marker) => Output
        .Split('\n')
        .Where(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase))
        .Select(l => l.Trim())
        .Distinct()
        .ToList();
}

/// <summary>
/// Drives the generators in-process and then compiles the result with the real SDK.
///
/// This is the only layer that catches "the generated code does not build" — the failure users
/// actually report, and the one that plan-level tests structurally cannot see.
/// </summary>
public sealed class ScaffoldHarness : IDisposable
{
    private readonly OnionWolverineErrorOrTemplate _template = new();

    public string Root { get; }

    public ScaffoldHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort on Windows file locks */ }
        GC.SuppressFinalize(this);
    }

    public string SolutionDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// <paramref name="targetFramework"/> defaults to null, meaning SolutionSpec's own default —
    /// but note that default is derived from the runtime of whatever process asks, and THIS
    /// process is the net8.0 test host. So every gate that does not pass a framework explicitly
    /// is exercising net8.0, regardless of the SDK installed or what a user would actually get.
    /// That is how a scaffold whose net10.0 configuration could not resolve passed every gate.
    /// </summary>
    public async Task<ExecutionResult> MakeSolution(
        string name, string roleGuardStyle, bool withBaseEntity = false, bool swagger = true,
        string? targetFramework = null)
    {
        SolutionDirectory = Path.Combine(Root, name);

        var stubs = new StubRepository(SolutionDirectory, ".forge/stubs", _template.SnippetFolder);
        var context = new SolutionScaffoldContext(stubs, SolutionDirectory, force: false);

        // DefaultTargetFramework, not a hardcoded TFM: the host-startup gate has to RUN the
        // generated app, and a TFM whose runtime is not installed fails with "You must install
        // or update .NET" rather than telling us anything about the generated code.
        var spec = new SolutionSpec(name, SolutionDirectory, roleGuardStyle, "wolverine",
            targetFramework ?? SolutionSpec.DefaultTargetFramework, "UserRole", null,
            WithBaseEntity: withBaseEntity, Swagger: swagger);

        return Apply(await _template.PlanSolution(context, spec, CancellationToken.None));
    }

    public Task<ExecutionResult> MakeEntity(
        string name, string properties, bool encapsulated = true, params string[] belongsTo) =>
        Run(ctx =>
        {
            var relations = RelationParser.Parse(belongsTo);
            Assert.True(relations.Ok, relations.Error);

            return _template.PlanEntity(ctx,
                new EntitySpec(name, PropertyParser.Parse(properties).Properties,
                    Encapsulated: encapsulated, Relations: relations.Relations),
                CancellationToken.None);
        });

    public Task<ExecutionResult> MakeEnum(string name, string values, bool flags = false) =>
        Run(ctx =>
        {
            var parsed = EnumValueParser.Parse(values, flags);
            Assert.True(parsed.Ok, parsed.Error);
            return _template.PlanEnum(ctx, new EnumSpec(name, parsed.Members, flags), CancellationToken.None);
        });

    public Task<ExecutionResult> MakeRepository(string entity) =>
        Run(ctx => _template.PlanRepository(ctx, entity, CancellationToken.None));

    public Task<ExecutionResult> MakeResource(string entity, string? audience = null, string? view = null,
        string[]? only = null, string[]? exclude = null) =>
        Run(ctx => _template.PlanResource(ctx,
            new ResourceSpec(entity, audience, view, only ?? [], exclude ?? []), CancellationToken.None));

    public Task<ExecutionResult> MakeFeature(
        string name, MessageKind kind, string[] roles, string? group = null,
        string? properties = null, string? returns = null, bool anonymous = false) =>
        Run(ctx => _template.PlanFeature(ctx, new FeatureSpec(
            name, kind, PropertyParser.Parse(properties).Properties,
            roles, anonymous, group, returns, false, null), CancellationToken.None));

    private async Task<ExecutionResult> Run(Func<TemplateContext, Task<PlanResult>> plan)
    {
        var loaded = ConfigLoader.Load(SolutionDirectory);
        Assert.True(loaded.Ok, loaded.Error);

        var context = new TemplateContext(
            loaded.Config!, loaded.SolutionRoot!,
            CodeStyle.Detect(Path.Combine(loaded.SolutionRoot!, loaded.Config!.ApplicationProject)),
            new StubRepository(loaded.SolutionRoot!, loaded.Config.StubOverridesPath, _template.SnippetFolder),
            force: false);

        return Apply(await plan(context));
    }

    private static ExecutionResult Apply(PlanResult result)
    {
        Assert.True(result.Ok, result.Error);

        var execution = new PlanExecutor().Apply(result.Plan);
        Assert.True(execution.Ok, execution.Error);
        return execution;
    }

    /// <summary>
    /// Builds the generated app's real service provider via EF's design-time host.
    ///
    /// This is the only check that catches a DI registration gap: a repository added to the
    /// UnitOfWork constructor with nothing registering it COMPILES fine and only fails when the
    /// host starts. Compilation alone cannot see it.
    /// </summary>
    public BuildOutcome VerifyHostStarts()
    {
        var config = ConfigLoader.Load(SolutionDirectory).Config!;
        return RunDotnet("ef", "dbcontext", "info",
            "--project", config.InfrastructureProject,
            "--startup-project", config.ApiProject,
            "--context", config.ResolvedDbContextName);
    }

    public BuildOutcome Build() => RunDotnet("build", "--nologo", "-v", "q");

    /// <summary>
    /// Adds a package to one of the generated projects, the way a developer would.
    ///
    /// Exists to test a property the scaffold has to have but that building it can never show:
    /// that the dependency graph it hands over can still MOVE. A set of pins can restore, build
    /// and run perfectly and still be unable to accept the next package the developer needs.
    /// </summary>
    public BuildOutcome AddPackage(string projectSuffix, string package, string version) =>
        RunDotnet("add", Path.Combine("src", projectSuffix), "package", package, "--version", version);

    private BuildOutcome RunDotnet(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = SolutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        // The test host injects MSBuild/dotnet variables that leak into child `dotnet` processes
        // and change SDK and framework resolution — dotnet-ef's own ef.dll targets net8.0 and
        // fails to launch under them even though it runs fine from a shell. Clear them, and let
        // roll-forward apply so a tool built for an older major still runs.
        foreach (var key in info.Environment.Keys
                     .Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)
                              || k.StartsWith("DOTNET_HOST", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            info.Environment.Remove(key);
        }

        info.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";

        using var process = Process.Start(info)!;

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        if (!process.WaitForExit(300_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new BuildOutcome(-1, output + "\n[timed out]");
        }

        return new BuildOutcome(process.ExitCode, output);
    }
}
