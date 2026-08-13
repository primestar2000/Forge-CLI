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
            (config, root, parse) => EfTool.MigrationsAdd(config, root, parse.GetValue(MigrationName)!));

        yield return Make("db:migrate", "Apply pending migrations to the database.",
            _ => { },
            (config, root, _) => EfTool.DatabaseUpdate(config, root));

        yield return Make("db:rollback", "Revert the database to an earlier migration.",
            command => command.Options.Add(ToOption),
            (config, root, parse) => EfTool.DatabaseUpdate(config, root, parse.GetValue(ToOption) ?? "0"));

        yield return Make("db:status", "List migrations and show which are applied.",
            _ => { },
            (config, root, _) => EfTool.MigrationsList(config, root));

        yield return BuildFresh();
        yield return BuildSeed();
    }

    private static readonly Option<string> OnlyOption =
        new("--only") { Description = "Run just this seeder, by ISeeder.Name." };

    private static readonly Option<bool> NoBuildOption =
        new("--no-build") { Description = "Skip building the app first. Only when you know the output is fresh." };

    /// <summary>
    /// db:seed is Tier 2: seeders need the real DbContext from the real container, which only
    /// exists inside the user's own process. forge therefore launches their app rather than
    /// loading their assemblies — see RuntimeBridge.
    /// </summary>
    private static Command BuildSeed()
    {
        var command = new Command("db:seed", "Run registered ISeeder implementations inside your application.");
        command.Options.Add(OnlyOption);
        command.Options.Add(NoBuildOption);
        command.WithGlobals();
        command.SetAction(parse =>
        {
            var json = parse.GetValue(GlobalOptions.Json);
            var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

            var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
            if (!loaded.Ok)
            {
                if (json) output.Json(Payload("db:seed", loaded.ExitCode, null, loaded.Error));
                else output.Failure($"x {loaded.Error}");
                return loaded.ExitCode;
            }

            var config = loaded.Config!;
            var root = loaded.SolutionRoot!;

            if (!RuntimeBridge.IsReferenced(config, root))
            {
                var error = $"{RuntimeBridge.PackageId} is not referenced by {config.ApiProject}, " +
                            "so db:seed is unavailable (Tier 2).";
                if (json) output.Json(Payload("db:seed", ExitCodes.ConfigInvalid, null, error));
                else
                {
                    output.Failure($"x {error}");
                    output.Dim("  -> forge runtime:install");
                    output.Dim("     (adds the package and wires Program.cs; --dry-run to preview)");
                }
                return ExitCodes.ConfigInvalid;
            }

            var arguments = new List<string>();
            var only = parse.GetValue(OnlyOption);
            if (!string.IsNullOrWhiteSpace(only)) { arguments.Add("--forge-only"); arguments.Add(only!); }

            if (parse.GetValue(GlobalOptions.DryRun))
            {
                var display = $"dotnet run --project {config.ApiProject} -- --forge:seed {string.Join(" ", arguments)}".TrimEnd();
                if (json) output.Json(Payload("db:seed", ExitCodes.Success, display));
                else
                {
                    output.Info("Dry run - nothing was executed.");
                    output.Dim($"  {display}");
                }
                return ExitCodes.Success;
            }

            if (!json) output.Dim($"  running seeders inside {config.ApiProject} ...");

            var result = RuntimeBridge.Invoke(config, root, "seed", arguments,
                parse.GetValue(NoBuildOption), output, verbose: !json);

            if (!result.Ok)
            {
                if (json) output.Json(Payload("db:seed", ExitCodes.Error, null, result.Error));
                else output.Failure($"x {result.Error}");
                return ExitCodes.Error;
            }

            if (json)
            {
                output.Json(result.Payload!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return ExitCodes.Success;
            }

            RenderSeeders(output, result.Data);
            return ExitCodes.Success;
        });
        return command;
    }

    private static void RenderSeeders(Output output, JsonNode? data)
    {
        if (data?["seeders"] is not JsonArray seeders || seeders.Count == 0)
        {
            output.Warn("No ISeeder implementations are registered.");
            output.Dim("  -> services.AddScoped<ISeeder, MySeeder>();");
            return;
        }

        foreach (var seeder in seeders.OfType<JsonObject>())
        {
            var name = seeder["name"]?.GetValue<string>() ?? "(unnamed)";
            var milliseconds = seeder["milliseconds"]?.GetValue<int>() ?? 0;
            var affected = seeder["affected"];

            var detail = affected is null ? $"{milliseconds}ms" : $"{affected} record(s), {milliseconds}ms";
            output.Success($"seeded   {name}  ({detail})");
        }

        output.Blank();
        output.Info($"{seeders.Count} seeder(s) ran.");
    }

    private static Command Make(
        string name,
        string description,
        Action<Command> configure,
        Func<ForgeConfig, string, ParseResult, EfCommand> build)
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

            var drop = Execute(parse, "db:fresh", (config, root, _) => EfTool.DatabaseDrop(config, root), destructive: true);
            if (drop != ExitCodes.Success) return drop;

            return Execute(parse, "db:fresh", (config, root, _) => EfTool.DatabaseUpdate(config, root), destructive: true);
        });
        return command;
    }

    private static int Execute(
        ParseResult parse,
        string commandName,
        Func<ForgeConfig, string, ParseResult, EfCommand> build,
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

        var command = build(loaded.Config!, loaded.SolutionRoot!, parse);

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

        // Runs from the API project directory, not the solution root, so a relative SQLite
        // connection string resolves to the same file `dotnet run` uses. See
        // EfTool.WorkingDirectoryFor.
        return EfTool.Run(command, EfTool.WorkingDirectoryFor(loaded.Config!, loaded.SolutionRoot!), output);
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
