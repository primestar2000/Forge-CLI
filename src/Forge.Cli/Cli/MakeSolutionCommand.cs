using System.CommandLine;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Cli;

/// <summary>
/// make:solution — one-time scaffold of a whole solution.
///
/// Like init, this cannot go through CommandRunner: that pipeline loads forge.config.json first,
/// and this command creates it.
/// </summary>
public static class MakeSolutionCommand
{
    private static readonly Option<string> NameOption =
        new("--name", "-n") { Description = "Solution name (e.g. OfficeCommute).", Required = true };

    private static readonly Option<string> OutputOption =
        new("--output", "-o") { Description = "Directory to scaffold into. Defaults to ./<name>." };

    private static readonly Option<string> TemplateOption =
        new("--template") { Description = "Template id. Defaults to onion-wolverine-erroror." };

    private static readonly Option<string> RoleGuardOption =
        new("--role-guard") { Description = "single-array (default) or role-and-subrole." };

    private static readonly Option<string> SchedulerOption =
        new("--scheduler") { Description = "wolverine (default), hangfire or quartz." };

    private static readonly Option<string> FrameworkOption =
        new("--framework", "-f")
        {
            Description = $"Target framework for generated projects. Defaults to {SolutionSpec.DefaultTargetFramework} " +
                          "(the runtime forge is running on, so the result is guaranteed runnable)."
        };

    private static readonly Option<bool> WithRuntimeOption =
        new("--with-runtime")
        {
            Description = "Reference Pitechy.Forge.Runtime and wire the hook into Program.cs, " +
                          "enabling db:seed and invoke:* (Tier 2)."
        };

    private static readonly Option<bool> WithBaseEntityOption =
        new("--with-base-entity")
        {
            Description = "Generate a BaseEntity carrying Id and audit timestamps, and derive " +
                          "every generated entity from it."
        };

    /// <summary>
    /// Negative flag rather than --with-swagger, because Swagger is on by default. The other
    /// --with-* flags gate things that can break a restore or change the architecture; this one
    /// only removes an OpenAPI document from an HTTP API that would otherwise have one.
    /// </summary>
    private static readonly Option<bool> NoSwaggerOption =
        new("--no-swagger")
        {
            Description = "Do not wire Swashbuckle and the /swagger UI into the API project. " +
                          "Swagger is included by default; swagger:install adds it later."
        };

    private static readonly Option<bool> PinForgeOption =
        new("--pin-forge")
        {
            Description = "Also write .config/dotnet-tools.json pinning this forge version. " +
                          "Only use when this version is restorable from a configured feed."
        };

    public static Command Build()
    {
        var command = new Command("make:solution",
            "Scaffold a full onion-architecture solution, wired and ready to build.");

        foreach (var option in new Option[] { NameOption, OutputOption, TemplateOption, RoleGuardOption, SchedulerOption, FrameworkOption, PinForgeOption, WithRuntimeOption, WithBaseEntityOption, NoSwaggerOption })
            command.Options.Add(option);

        command.WithGlobals();
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult parse)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var dryRun = parse.GetValue(GlobalOptions.DryRun);
        var force = parse.GetValue(GlobalOptions.Force);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var name = parse.GetValue(NameOption)!;
        var templateName = Or(parse.GetValue(TemplateOption), "onion-wolverine-erroror");

        var template = TemplateRegistry.Resolve(templateName);
        if (template is null)
        {
            return Fail(output, ExitCodes.UsageError, ".",
                $"Unknown template '{templateName}'. Known: {string.Join(", ", TemplateRegistry.Names)}.");
        }

        var root = Path.GetFullPath(Or(parse.GetValue(OutputOption), Path.Combine(Directory.GetCurrentDirectory(), name)));

        var spec = new SolutionSpec(
            Name: name,
            Directory: root,
            RoleGuardStyle: Or(parse.GetValue(RoleGuardOption), "single-array"),
            Scheduler: Or(parse.GetValue(SchedulerOption), "wolverine"),
            TargetFramework: Or(parse.GetValue(FrameworkOption), SolutionSpec.DefaultTargetFramework),
            RoleEnum: "UserRole",
            DbContextName: null,
            PinForge: parse.GetValue(PinForgeOption),
            WithRuntime: parse.GetValue(WithRuntimeOption),
            WithBaseEntity: parse.GetValue(WithBaseEntityOption),
            Swagger: !parse.GetValue(NoSwaggerOption));

        // Stubs resolve against the solution being created, so a team can pre-seed .forge/stubs.
        var stubs = new StubRepository(root, ".forge/stubs", template.SnippetFolder);
        var context = new SolutionScaffoldContext(stubs, root, force);

        PlanResult result;
        try
        {
            result = template.PlanSolution(context, spec, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (StubException ex)
        {
            return Fail(output, ExitCodes.Error, root, ex.Message);
        }

        if (!result.Ok) return Fail(output, result.ExitCode, root, result.Error!);

        if (dryRun)
        {
            if (json) output.Json(JsonOutput.Render("make:solution", result.Plan, ExitCodes.Success, root));
            else
            {
                output.Info($"Dry run - no files were written. Target: {root}");
                output.Blank();
                foreach (var action in result.Plan.Actions)
                {
                    var relative = Path.GetRelativePath(root, action.Path).Replace('\\', '/');
                    if (action is FileAction.Skip skip) output.Dim($"- skip   {relative} ({skip.Reason})");
                    else output.Success($"+ create {relative}");
                }
            }
            return ExitCodes.Success;
        }

        var execution = new PlanExecutor().Apply(result.Plan);
        if (!execution.Ok) return Fail(output, execution.ExitCode, root, execution.Error!);

        var exitCode = result.Plan.IsEntirelySkipped ? ExitCodes.TargetExists : ExitCodes.Success;

        if (json)
        {
            output.Json(JsonOutput.Render("make:solution", result.Plan, exitCode, root));
            return exitCode;
        }

        foreach (var action in result.Plan.Actions)
        {
            var relative = Path.GetRelativePath(root, action.Path).Replace('\\', '/');
            if (action is FileAction.Skip skip) output.Warn($"skipped  {relative} ({skip.Reason})");
            else output.Success($"created  {relative}");
        }

        output.Blank();
        output.Info("Next:");
        output.Dim($"  cd {Path.GetRelativePath(Directory.GetCurrentDirectory(), root).Replace('\\', '/')}");
        output.Dim("  dotnet build");
        output.Dim("  dotnet forge doctor");
        output.Dim("  dotnet forge make:repo -i <Entity> --dry-run");
        return exitCode;
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static int Fail(Output output, int exitCode, string root, string error)
    {
        if (output.JsonMode) output.Json(JsonOutput.Render("make:solution", GenerationPlan.Empty, exitCode, root, error));
        else output.Failure($"x {error}");
        return exitCode;
    }
}
