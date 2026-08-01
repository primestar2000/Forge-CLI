using System.CommandLine;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Cli;

/// <summary>
/// Shared pipeline for every generator command: load config -> build context -> plan ->
/// render or apply. Keeping it in one place is what makes --dry-run, --json and the exit-code
/// contract behave identically across commands instead of per-command approximations.
/// </summary>
public static class CommandRunner
{
    public static async Task<int> RunGenerator(
        ParseResult parse,
        string commandName,
        Func<ITemplate, TemplateContext, CancellationToken, Task<PlanResult>> plan,
        CancellationToken ct = default)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var dryRun = parse.GetValue(GlobalOptions.DryRun);
        var force = parse.GetValue(GlobalOptions.Force);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
        if (!loaded.Ok)
        {
            return Fail(output, commandName, loaded.ExitCode, loaded.Error!, loaded.SolutionRoot ?? ".");
        }

        var config = loaded.Config!;
        var root = loaded.SolutionRoot!;

        var template = TemplateRegistry.Resolve(config.Template);
        if (template is null)
        {
            return Fail(output, commandName, ExitCodes.ConfigInvalid,
                $"Unknown template '{config.Template}'. Known templates: {string.Join(", ", TemplateRegistry.Names)}.",
                root);
        }

        var codeStyle = CodeStyle.Detect(Path.Combine(root, config.ApplicationProject));
        var stubs = new StubRepository(root, config.StubOverridesPath, template.SnippetFolder);
        var manifest = GeneratedManifest.Load(root);
        var context = new TemplateContext(config, root, codeStyle, stubs, force)
        {
            Manifest = manifest,
            OverwriteModified = parse.GetValue(GlobalOptions.OverwriteModified)
        };

        PlanResult result;
        try
        {
            result = await plan(template, context, ct);
        }
        catch (StubException ex)
        {
            return Fail(output, commandName, ExitCodes.Error, ex.Message, root);
        }

        if (!result.Ok)
        {
            return Fail(output, commandName, result.ExitCode, result.Error!, root);
        }

        // ---- dry run: render the plan, write nothing.
        if (dryRun)
        {
            if (json)
            {
                output.Json(JsonOutput.Render(commandName, result.Plan, ExitCodes.Success, root));
                return ExitCodes.Success;
            }
            RenderDryRun(output, result.Plan, root);
            return ExitCodes.Success;
        }

        var execution = new PlanExecutor().Apply(result.Plan, manifest, commandName);
        if (!execution.Ok)
        {
            return Fail(output, commandName, execution.ExitCode, execution.Error!, root);
        }

        // Everything skipped means "already done" — benign, but distinguishable from success so a
        // pipeline can tell a re-run from real work. A protected skip means forge actively
        // REFUSED a requested write, which must never be silently reported as full success.
        var exitCode = result.Plan.IsEntirelySkipped || result.Plan.HasProtectedSkips
            ? ExitCodes.TargetExists
            : ExitCodes.Success;

        if (json)
        {
            output.Json(JsonOutput.Render(commandName, result.Plan, exitCode, root));
            return exitCode;
        }

        RenderSummary(output, result.Plan, root);
        return exitCode;
    }

    private static void RenderDryRun(Output output, GenerationPlan plan, string root)
    {
        output.Info("Dry run — no files were written.");
        output.Blank();

        foreach (var action in plan.Actions)
        {
            var relative = Path.GetRelativePath(root, action.Path).Replace('\\', '/');
            switch (action)
            {
                case FileAction.Create create:
                    output.Success($"+ create {relative}");
                    output.Diff(LineDiff.Render(string.Empty, create.Content));
                    break;

                case FileAction.Patch patch:
                    output.Success($"~ patch  {relative}");
                    output.Diff(LineDiff.Render(patch.Before, patch.After));
                    break;

                case FileAction.Skip skip:
                    output.Dim($"- skip   {relative} ({skip.Reason})");
                    break;
            }
            output.Blank();
        }
    }

    private static void RenderSummary(Output output, GenerationPlan plan, string root)
    {
        foreach (var action in plan.Actions)
        {
            var relative = Path.GetRelativePath(root, action.Path).Replace('\\', '/');
            switch (action)
            {
                case FileAction.Create:
                    output.Success($"created  {relative}");
                    break;
                case FileAction.Patch:
                    output.Success($"wired    {relative}");
                    break;
                case FileAction.Skip skip:
                    output.Warn($"skipped  {relative} ({skip.Reason})");
                    break;
            }
        }

        if (plan.IsEmpty) output.Warn("Nothing to do.");
    }

    private static int Fail(Output output, string command, int exitCode, string error, string root)
    {
        if (output.JsonMode)
        {
            output.Json(JsonOutput.Render(command, GenerationPlan.Empty, exitCode, root, error));
        }
        else
        {
            output.Failure($"x {error}");
        }
        return exitCode;
    }
}
