using System.CommandLine;
using System.Text.Json.Nodes;
using Forge.Cli.Config;

namespace Forge.Cli.Cli;

/// <summary>
/// db:* — thin, correctly-targeted wrappers over dotnet-ef (Tier 1).
///
/// None of these produce a GenerationPlan, so they do not go through CommandRunner. --dry-run is
/// still honoured: it prints the exact `dotnet ef` command that would run, which doubles as a
/// way to learn the underlying invocation.
/// </summary>
public static class DbCommands
{
    private static readonly Argument<string> MigrationName =
        new("name") { Description = "Migration name, e.g. AddOrderTable." };

    private static readonly Option<string> ToOption =
        new("--to") { Description = "Target migration to roll back to. Use 0 to revert everything." };

    private static readonly Option<bool> ProductionOverride =
        new("--i-know-this-is-production")
        { Description = "Required to run a destructive command when the environment is Production." };

    public static IEnumerable<Command> Build()
    {
        yield return Make("db:migration", "Create a new EF Core migration, with project paths resolved from config.",
            command => command.Arguments.Add(MigrationName),
            (config, parse) => EfTool.MigrationsAdd(config, parse.GetValue(MigrationName)!));

        yield return Make("db:migrate", "Apply pending migrations to the database.",
            _ => { },
            (config, _) => EfTool.DatabaseUpdate(config));

        yield return Make("db:rollback", "Revert the database to an earlier migration.",
            command => command.Options.Add(ToOption),
            (config, parse) => EfTool.DatabaseUpdate(config, parse.GetValue(ToOption) ?? "0"));

        yield return Make("db:status", "List migrations and show which are applied.",
            _ => { },
            (config, _) => EfTool.MigrationsList(config));

        yield return BuildFresh();
    }

    private static Command Make(
        string name,
        string description,
        Action<Command> configure,
        Func<ForgeConfig, ParseResult, EfCommand> build)
    {
        var command = new Command(name, description);
        configure(command);
        command.WithGlobals();
        command.SetAction(parse => Execute(parse, name, build, destructive: false));
        return command;
    }

    /// <summary>
    /// db:fresh drops and recreates the database. It carries two independent brakes: an explicit
    /// --force, and a hard refusal when the environment is Production.
    /// </summary>
    private static Command BuildFresh()
    {
        var command = new Command("db:fresh",
            "Drop, recreate and re-migrate the database. Local/dev only; requires --force.");
        command.Options.Add(ProductionOverride);
        command.WithGlobals();
        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(GlobalOptions.Json), parse.GetValue(GlobalOptions.NoColor));

            if (!parse.GetValue(GlobalOptions.Force))
            {
                output.Failure("x db:fresh destroys the database and all of its data.");
                output.Dim("  -> Re-run with --force if that is what you want.");
                return ExitCodes.UsageError;
            }

            var drop = Execute(parse, "db:fresh", (config, _) => EfTool.DatabaseDrop(config), destructive: true);
            if (drop != ExitCodes.Success) return drop;

            return Execute(parse, "db:fresh", (config, _) => EfTool.DatabaseUpdate(config), destructive: true);
        });
        return command;
    }

    private static int Execute(
        ParseResult parse,
        string commandName,
        Func<ForgeConfig, ParseResult, EfCommand> build,
        bool destructive)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var dryRun = parse.GetValue(GlobalOptions.DryRun);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
        if (!loaded.Ok)
        {
            if (json) output.Json(Payload(commandName, loaded.ExitCode, null, loaded.Error));
            else output.Failure($"x {loaded.Error}");
            return loaded.ExitCode;
        }

        if (destructive && EfTool.IsProduction(out var environment)
                        && !parse.GetValue(ProductionOverride))
        {
            var error = $"Refusing to run {commandName}: environment is '{environment}'.";
            if (json) output.Json(Payload(commandName, ExitCodes.EnvironmentGuard, null, error));
            else
            {
                output.Failure($"x {error}");
                output.Dim("  -> This command destroys data. If you are certain, pass --i-know-this-is-production.");
            }
            return ExitCodes.EnvironmentGuard;
        }

        var command = build(loaded.Config!, parse);

        if (dryRun)
        {
            if (json) output.Json(Payload(commandName, ExitCodes.Success, command.Display));
            else
            {
                output.Info("Dry run - nothing was executed.");
                output.Dim($"  {command.Display}");
            }
            return ExitCodes.Success;
        }

        // dotnet-ef reads project metadata and loads the startup project, so an unrestored
        // solution fails with a raw NETSDK1004 about a missing assets file. Say what to do.
        var assets = Path.Combine(loaded.SolutionRoot!, loaded.Config!.InfrastructureProject, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            var error = $"{loaded.Config.InfrastructureProject} has not been restored, so dotnet-ef cannot read it.";
            if (json) output.Json(Payload(commandName, ExitCodes.ConfigInvalid, command.Display, error));
            else
            {
                output.Failure($"x {error}");
                output.Dim("  -> dotnet build      # restores and builds the solution first");
            }
            return ExitCodes.ConfigInvalid;
        }

        if (!EfTool.IsInstalled())
        {
            const string error = "dotnet-ef is not installed, so db:* commands are unavailable (Tier 1).";
            if (json) output.Json(Payload(commandName, ExitCodes.ConfigInvalid, command.Display, error));
            else
            {
                output.Failure($"x {error}");
                output.Dim("  -> dotnet tool install --global dotnet-ef");
            }
            return ExitCodes.ConfigInvalid;
        }

        if (!json) output.Dim($"  {command.Display}");

        return EfTool.Run(command, loaded.SolutionRoot!, output);
    }

    private static string Payload(string command, int exitCode, string? invocation, string? error = null)
    {
        var payload = new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["schemaVersion"] = JsonOutput.SchemaVersion,
            ["command"] = command,
            ["success"] = exitCode == ExitCodes.Success,
            ["exitCode"] = exitCode,
            ["invocation"] = invocation
        };

        var diagnostics = new JsonArray();
        if (!string.IsNullOrEmpty(error)) diagnostics.Add(error);
        payload["diagnostics"] = diagnostics;

        return payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
