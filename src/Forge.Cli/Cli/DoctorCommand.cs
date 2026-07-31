using System.CommandLine;
using System.Text.Json.Nodes;
using Forge.Cli.Config;
using Forge.Cli.Diagnostics;
using Forge.Cli.Diagnostics.Checks;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Cli;

/// <summary>
/// forge doctor — the fresh-machine diagnostic and the config-drift detector.
///
/// Reports which command TIER is currently available rather than hard-failing: Tier 0 (all of
/// make:*, stub:*, config:*) needs nothing but the SDK, so a missing dotnet-ef or Forge.Runtime
/// is a warning, never an error. Every problem carries the command that fixes it.
/// </summary>
public static class DoctorCommand
{
    private static readonly Option<bool> CheckOption =
        new("--check") { Description = "Exit non-zero if any check fails - for CI." };

    public static Command Build()
    {
        var command = new Command("doctor",
            "Health check: environment, config, and drift between config and the codebase.");
        command.Options.Add(CheckOption);
        command.WithGlobals();
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult parse)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var checkMode = parse.GetValue(CheckOption);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
        var root = loaded.SolutionRoot ?? Directory.GetCurrentDirectory();
        var context = new DoctorContext(loaded.Config, root);

        var template = loaded.Config is not null ? TemplateRegistry.Resolve(loaded.Config.Template) : null;
        if (loaded.Config is not null && template is not null)
        {
            context.Stubs = new StubRepository(root, loaded.Config.StubOverridesPath, template.SnippetFolder);
        }

        var results = new List<CheckResult>();
        IDoctorCheck[] checks = [new EnvironmentCheck(), new ConfigCheck(), new RuntimePackageCheck(), new StubDriftCheck()];

        foreach (var check in checks)
        {
            try { results.AddRange(check.Run(context)); }
            catch (Exception ex)
            {
                results.Add(CheckResult.Fail(check.GetType().Name, $"check threw: {ex.Message}"));
            }
        }

        // Template-specific checks come last; they are the ones that need a valid config.
        if (template is not null)
        {
            try { results.AddRange(template.Diagnose(context)); }
            catch (Exception ex)
            {
                results.Add(CheckResult.Fail("template checks", $"threw: {ex.Message}"));
            }
        }

        var tier = DetermineTier(results, loaded.Config is not null);

        if (json)
        {
            output.Json(RenderJson(results, tier));
        }
        else
        {
            Render(output, results, tier);
        }

        var failed = results.Count(r => r.Status == CheckStatus.Fail);
        if (checkMode && failed > 0) return ExitCodes.ConfigInvalid;
        return ExitCodes.Success;
    }

    private static Tier DetermineTier(List<CheckResult> results, bool hasConfig)
    {
        if (!hasConfig || results.Any(r => r.Status == CheckStatus.Fail && r.Label is "forge.config.json" or "config paths"))
            return Tier.None;

        var efOk = results.Any(r => r.Label == "dotnet-ef" && r.Status == CheckStatus.Pass);
        var runtimeOk = results.Any(r => r.Label == "Forge.Runtime" && r.Status == CheckStatus.Pass);

        if (runtimeOk && efOk) return Tier.Runtime;
        if (efOk) return Tier.Migrations;
        return Tier.Scaffolding;
    }

    private static void Render(Output output, List<CheckResult> results, Tier tier)
    {
        output.Info($"forge {ForgeVersion.Current} doctor");
        output.Blank();

        var width = results.Count == 0 ? 0 : results.Max(r => r.Label.Length) + 2;

        foreach (var result in results)
        {
            var line = $"{Symbol(result.Status)} {result.Label.PadRight(width)}{result.Detail}";
            switch (result.Status)
            {
                case CheckStatus.Pass: output.Success(line); break;
                case CheckStatus.Warn: output.Warn(line); break;
                case CheckStatus.Fail: output.Failure(line); break;
                default: output.Dim(line); break;
            }

            if (result.Fix is not null) output.Dim($"    -> {result.Fix}");
        }

        output.Blank();
        output.Info(TierSummary(tier));

        var failed = results.Count(r => r.Status == CheckStatus.Fail);
        if (failed > 0)
        {
            output.Blank();
            output.Failure($"{failed} check(s) failed - generators will not work correctly until these are fixed.");
        }
    }

    private static string TierSummary(Tier tier) => tier switch
    {
        Tier.Runtime => "Tier 2 - all commands available.",
        Tier.Migrations => "Tier 1 - make:*, stub:*, config:*, db:migrate/migration available. invoke:* and db:seed need Forge.Runtime.",
        Tier.Scaffolding => "Tier 0 - make:*, stub:*, config:* available. db:* needs dotnet-ef; invoke:* needs Forge.Runtime.",
        _ => "No tier available - fix the failures above, or run 'forge init'."
    };

    private static string Symbol(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "v",
        CheckStatus.Warn => "!",
        CheckStatus.Fail => "x",
        _ => "-"
    };

    private static string RenderJson(List<CheckResult> results, Tier tier)
    {
        var array = new JsonArray();
        foreach (var result in results)
        {
            var node = new JsonObject
            {
                ["label"] = result.Label,
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["detail"] = result.Detail
            };
            if (result.Fix is not null) node["fix"] = result.Fix;
            array.Add(node);
        }

        var payload = new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["schemaVersion"] = JsonOutput.SchemaVersion,
            ["command"] = "doctor",
            ["tier"] = (int)tier,
            ["failed"] = results.Count(r => r.Status == CheckStatus.Fail),
            ["warned"] = results.Count(r => r.Status == CheckStatus.Warn),
            ["checks"] = array
        };

        return payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
