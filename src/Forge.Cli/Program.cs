using System.CommandLine;
using System.Text.Json;
using Forge.Cli.Cli;
using Forge.Cli.Config;

// System.CommandLine 2.0 GA API: SetAction / Options.Add / Subcommands.Add / Parse().Invoke().
// Beta spellings (SetHandler, AddOption, AddCommand) do not compile against 2.0.10.

var root = new RootCommand("forge — architectural code generator for ASP.NET Core");

// ------------------------------------------------------------------------- make:solution
root.Subcommands.Add(MakeSolutionCommand.Build());

// ---------------------------------------------------------------------------- make:repo
var entityOption = new Option<string>("--entity", "-i")
{
    Description = "Entity the repository is for (e.g. Gig).",
    Required = true
};

var allowMissingEntity = new Option<bool>("--allow-missing-entity")
{
    Description = "Generate even if the entity type is not found in the domain project."
};

var makeRepo = new Command("make:repo", "Scaffold I{Entity}Repository + {Entity}Repository and wire them into IUnitOfWork.");
makeRepo.Options.Add(entityOption);
makeRepo.Options.Add(allowMissingEntity);
makeRepo.WithGlobals();
makeRepo.SetAction((parse, ct) =>
    CommandRunner.RunGenerator(parse, "make:repo",
        (template, context, token) =>
        {
            context.AllowMissingEntity = parse.GetValue(allowMissingEntity);
            return template.PlanRepository(context, parse.GetValue(entityOption)!, token);
        }, ct));

root.Subcommands.Add(makeRepo);

// --------------------------------------------------------------------- init / doctor
root.Subcommands.Add(InitCommand.Build());
root.Subcommands.Add(DoctorCommand.Build());

// ---------------------------------------------------------------------------- config:show
var configShow = new Command("config:show", "Print the resolved forge.config.json and the detected solution root.");
configShow.WithGlobals();
configShow.SetAction(parse =>
{
    var output = new Output(parse.GetValue(GlobalOptions.Json), parse.GetValue(GlobalOptions.NoColor));
    var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
    if (!loaded.Ok)
    {
        output.Failure($"x {loaded.Error}");
        return loaded.ExitCode;
    }

    if (output.JsonMode)
    {
        output.Json(JsonSerializer.Serialize(loaded.Config, ConfigLoader.JsonOptions));
        return ExitCodes.Success;
    }

    output.Info($"solution root : {loaded.SolutionRoot}");
    output.Info($"template      : {loaded.Config!.Template}");
    output.Info($"config version: {loaded.Config.Version}");
    output.Blank();
    output.Info(JsonSerializer.Serialize(loaded.Config, ConfigLoader.JsonOptions));
    return ExitCodes.Success;
});

root.Subcommands.Add(configShow);

// ------------------------------------------------------------------------ config:validate
var configValidate = new Command("config:validate", "Check every configured path exists and every value is legal.");
configValidate.WithGlobals();
configValidate.SetAction(parse =>
{
    var output = new Output(parse.GetValue(GlobalOptions.Json), parse.GetValue(GlobalOptions.NoColor));
    var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
    if (!loaded.Ok)
    {
        output.Failure($"x {loaded.Error}");
        return loaded.ExitCode;
    }

    var problems = ConfigLoader.Validate(loaded.Config!, loaded.SolutionRoot!);
    if (problems.Count == 0)
    {
        output.Success("v forge.config.json is valid — every configured path resolves.");
        return ExitCodes.Success;
    }

    foreach (var problem in problems) output.Failure($"x {problem}");
    return ExitCodes.ConfigInvalid;
});

root.Subcommands.Add(configValidate);

// ---------------------------------------------------------------------------------- list
var list = new Command("list", "List every command, grouped by namespace.");
list.WithGlobals();
list.SetAction(parse =>
{
    GroupedHelp.Render(root, new Output(false, parse.GetValue(GlobalOptions.NoColor)));
    return ExitCodes.Success;
});

root.Subcommands.Add(list);

return root.Parse(args).Invoke();
