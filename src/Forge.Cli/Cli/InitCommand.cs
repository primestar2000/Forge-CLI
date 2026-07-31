using System.CommandLine;
using System.Text.Json;
using Forge.Cli.Config;
using Forge.Cli.Planning;

namespace Forge.Cli.Cli;

/// <summary>
/// forge init — adopt forge into an existing solution by inferring forge.config.json from what is
/// actually in the codebase.
///
/// Does not go through CommandRunner: that pipeline loads forge.config.json first, and this is the
/// command that creates it. It still produces a GenerationPlan so --dry-run and --json behave
/// exactly as they do everywhere else.
/// </summary>
public static class InitCommand
{
    public static Command Build()
    {
        var command = new Command("init",
            "Infer forge.config.json from an existing solution (brownfield adoption).");
        command.WithGlobals();
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult parse)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var force = parse.GetValue(GlobalOptions.Force);
        var dryRun = parse.GetValue(GlobalOptions.DryRun);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var result = ConfigInferrer.Infer(Directory.GetCurrentDirectory());
        if (!result.Ok)
        {
            if (json) output.Json(JsonOutput.Render("init", GenerationPlan.Empty, ExitCodes.ConfigInvalid, result.SolutionRoot, result.Error));
            else output.Failure($"x {result.Error}");
            return ExitCodes.ConfigInvalid;
        }

        var target = Path.Combine(result.SolutionRoot, ConfigLoader.FileName);

        if (File.Exists(target) && !force)
        {
            var plan = GenerationPlan.Of(new FileAction.Skip(target, "already exists (use --force to regenerate)"));
            if (json)
            {
                output.Json(JsonOutput.Render("init", plan, ExitCodes.TargetExists, result.SolutionRoot));
            }
            else
            {
                output.Warn($"skipped  {ConfigLoader.FileName} already exists.");
                output.Dim("         Use --force to regenerate it, or edit it by hand.");
            }
            return ExitCodes.TargetExists;
        }

        var contents = JsonSerializer.Serialize(result.Config, ConfigLoader.JsonOptions) + Environment.NewLine;
        var writePlan = GenerationPlan.Of(new FileAction.Create(target, contents));

        if (!json) ReportInferences(output, result);

        if (dryRun)
        {
            if (json) output.Json(JsonOutput.Render("init", writePlan, ExitCodes.Success, result.SolutionRoot));
            else
            {
                output.Info("Dry run - no files were written.");
                output.Blank();
                output.Diff(LineDiff.Render(string.Empty, contents));
            }
            return ExitCodes.Success;
        }

        var execution = new PlanExecutor().Apply(writePlan);
        if (!execution.Ok)
        {
            if (json) output.Json(JsonOutput.Render("init", GenerationPlan.Empty, execution.ExitCode, result.SolutionRoot, execution.Error));
            else output.Failure($"x {execution.Error}");
            return execution.ExitCode;
        }

        if (json)
        {
            output.Json(JsonOutput.Render("init", writePlan, ExitCodes.Success, result.SolutionRoot));
            return ExitCodes.Success;
        }

        output.Blank();
        output.Success($"created  {ConfigLoader.FileName}");
        output.Blank();
        output.Info("Next:");
        output.Dim("  forge config:validate     # confirm every inferred path resolves");
        output.Dim("  forge make:repo -i <Name> --dry-run");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Prints what was inferred and why. Low-confidence rows are the ones a human must check —
    /// silently guessing a path and then failing halfway through a generator would be worse than
    /// not guessing at all.
    /// </summary>
    private static void ReportInferences(Output output, InferenceResult result)
    {
        output.Info($"Solution root: {result.SolutionRoot}");
        output.Blank();

        var width = result.Inferences.Max(i => i.Field.Length) + 2;
        var valueWidth = Math.Min(46, Math.Max(20, result.Inferences.Max(i => i.Value.Length) + 2));

        foreach (var inference in result.Inferences)
        {
            var line = $"  {inference.Field.PadRight(width)}{inference.Value.PadRight(valueWidth)}{inference.Evidence}";
            switch (inference.Confidence)
            {
                case Confidence.High: output.Success(line); break;
                case Confidence.Medium: output.Info(line); break;
                default: output.Warn(line); break;
            }
        }

        if (result.HasLowConfidence)
        {
            output.Blank();
            output.Warn("Rows above in yellow could not be inferred from the codebase and fell back to a default.");
            output.Warn("Check them before running any generator - a wrong path fails mid-write.");
        }
    }
}
