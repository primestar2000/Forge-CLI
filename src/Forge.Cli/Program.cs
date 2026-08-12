using System.CommandLine;
using System.Text.Json;
using Forge.Cli.Cli;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Templates;

// System.CommandLine 2.0 GA API: SetAction / Options.Add / Subcommands.Add / Parse().Invoke().
// Beta spellings (SetHandler, AddOption, AddCommand) do not compile against 2.0.10.

var root = new RootCommand("forge — architectural code generator for ASP.NET Core");

// ------------------------------------------------------------------------- make:solution
root.Subcommands.Add(MakeSolutionCommand.Build());

// -------------------------------------------------------------------------- make:entity
var entityNameOption = new Option<string>("--name", "-n")
{
    Description = "Entity name (e.g. Product).",
    Required = true
};

var propertiesOption = new Option<string>("--properties", "-p")
{
    Description = "Comma-separated Name:type list, e.g. \"Name:string,Price:decimal,Notes:string?\"."
};

var skipDbSetOption = new Option<bool>("--skip-dbset")
{
    Description = "Do not add a DbSet property to the DbContext."
};

var publicSettersOption = new Option<bool>("--public-setters")
{
    Description = "Emit { get; set; } instead of the default encapsulated entity " +
                  "(private setters + constructor + Update)."
};

var makeEntity = new Command("make:entity", "Scaffold a Domain entity plus its EF Core IEntityTypeConfiguration.");
makeEntity.Options.Add(entityNameOption);
makeEntity.Options.Add(propertiesOption);
makeEntity.Options.Add(skipDbSetOption);
makeEntity.Options.Add(publicSettersOption);
makeEntity.WithGlobals();
makeEntity.SetAction((parse, ct) =>
    CommandRunner.RunGenerator(parse, "make:entity", (template, context, token) =>
    {
        // Parse before planning so a malformed --properties is a clean usage error rather than
        // a failure halfway through building the plan.
        var parsed = PropertyParser.Parse(parse.GetValue(propertiesOption));
        if (!parsed.Ok) return Task.FromResult(PlanResult.UsageError(parsed.Error!));

        // Encapsulated is the default; the flag and the config key are both opt-outs, and an
        // explicit --public-setters beats the config so a one-off does not need an edit.
        var encapsulated = !parse.GetValue(publicSettersOption) && context.Config.EncapsulateEntities;

        var spec = new EntitySpec(
            parse.GetValue(entityNameOption)!,
            parsed.Properties,
            parse.GetValue(skipDbSetOption),
            encapsulated);

        return template.PlanEntity(context, spec, token);
    }, ct));

root.Subcommands.Add(makeEntity);

// ------------------------------------------------------------------------ make:resource
var resourceEntityOption = new Option<string>("--name", "-n")
{
    Description = "Entity the response projects (e.g. Order).",
    Required = true
};

var audienceOption = new Option<string>("--audience", "-a")
{
    Description = "Audience segment, e.g. public, customer, admin, partner."
};

var viewOption = new Option<string>("--view")
{
    Description = "View segment, e.g. summary (list) or detail (single record)."
};

var onlyOption = new Option<string[]>("--only")
{
    Description = "Include only these entity properties, in this order.",
    AllowMultipleArgumentsPerToken = true
};

var excludeOption = new Option<string[]>("--exclude")
{
    Description = "Include every entity property except these.",
    AllowMultipleArgumentsPerToken = true
};

var makeResource = new Command("make:resource",
    "Scaffold an audience-shaped response record plus its Mapster registration.");

foreach (var option in new Option[] { resourceEntityOption, audienceOption, viewOption, onlyOption, excludeOption })
    makeResource.Options.Add(option);

makeResource.WithGlobals();
makeResource.SetAction((parse, ct) =>
    CommandRunner.RunGenerator(parse, "make:resource", (template, context, token) =>
        template.PlanResource(context, new Forge.Cli.Templates.OnionWolverineErrorOr.ResourceSpec(
            Entity: parse.GetValue(resourceEntityOption)!,
            Audience: parse.GetValue(audienceOption),
            View: parse.GetValue(viewOption),
            Only: parse.GetValue(onlyOption) ?? [],
            Exclude: parse.GetValue(excludeOption) ?? []), token), ct));

root.Subcommands.Add(makeResource);

// ------------------------------------------------------------------------- make:feature
var featureNameOption = new Option<string>("--name", "-n")
{
    Description = "Feature name (e.g. CreateGig).",
    Required = true
};

var typeOption = new Option<string>("--type")
{
    Description = "command or query.",
    Required = true
};

var rolesOption = new Option<string[]>("--roles")
{
    Description = "Roles allowed to execute this message, e.g. --roles User Admin.",
    AllowMultipleArgumentsPerToken = true
};

var anonymousOption = new Option<bool>("--anonymous")
{
    Description = "Message is genuinely public; emits the role-check bypass marker instead."
};

var groupOption = new Option<string>("--group", "-g")
{
    Description = "Feature group folder, e.g. -g Gigs -> Features/Gigs/Commands/<Name>/."
};

var returnsOption = new Option<string>("--returns")
{
    Description = "Handler result type inside ErrorOr<>. Defaults to Success."
};

var featurePropsOption = new Option<string>("--properties", "-p")
{
    Description = "Message record parameters, e.g. \"Name:string,BudgetMin:int\"."
};

var withRepoOption = new Option<bool>("--with-repo")
{
    Description = "Also scaffold the repository pair for --entity (defaults to the feature name)."
};

var featureEntityOption = new Option<string>("--entity")
{
    Description = "Entity for --with-repo, when it differs from the feature name."
};

var makeFeature = new Command("make:feature", "Scaffold a command/query record, its Wolverine handler and a validator.");
foreach (var option in new Option[]
{
    featureNameOption, typeOption, rolesOption, anonymousOption, groupOption,
    returnsOption, featurePropsOption, withRepoOption, featureEntityOption
})
{
    makeFeature.Options.Add(option);
}
makeFeature.WithGlobals();
makeFeature.SetAction((parse, ct) =>
    CommandRunner.RunGenerator(parse, "make:feature", (template, context, token) =>
    {
        var typeText = parse.GetValue(typeOption)!;
        MessageKind kind;
        if (typeText.Equals("command", StringComparison.OrdinalIgnoreCase)) kind = MessageKind.Command;
        else if (typeText.Equals("query", StringComparison.OrdinalIgnoreCase)) kind = MessageKind.Query;
        else return Task.FromResult(PlanResult.UsageError($"--type must be 'command' or 'query', not '{typeText}'."));

        var parsed = PropertyParser.Parse(parse.GetValue(featurePropsOption));
        if (!parsed.Ok) return Task.FromResult(PlanResult.UsageError(parsed.Error!));

        var spec = new FeatureSpec(
            Name: parse.GetValue(featureNameOption)!,
            Kind: kind,
            Properties: parsed.Properties,
            Roles: (parse.GetValue(rolesOption) ?? []).Select(NameHelper.Pascal).ToList(),
            Anonymous: parse.GetValue(anonymousOption),
            Group: parse.GetValue(groupOption),
            Returns: parse.GetValue(returnsOption),
            WithRepo: parse.GetValue(withRepoOption),
            Entity: parse.GetValue(featureEntityOption));

        return template.PlanFeature(context, spec, token);
    }, ct));

root.Subcommands.Add(makeFeature);

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

// -------------------------------------------------------------------- runtime:install
// Separate from `make:solution --with-runtime` because the need is nearly always discovered
// after the solution exists — a creation-time flag cannot retrofit a solution already written.
var runtimeVersionOption = new Option<string?>("--version")
{
    Description = "Version of the Pitechy.Forge.Runtime packages (defaults to this forge's version)."
};

var runtimeInstall = new Command("runtime:install",
    "Wire the tier-2 runtime into this solution so db:seed and invoke:* work.");
runtimeInstall.Options.Add(runtimeVersionOption);
runtimeInstall.WithGlobals();
runtimeInstall.SetAction((parse, ct) =>
    CommandRunner.RunGenerator(parse, "runtime:install",
        (template, context, token) =>
            template.PlanRuntimeInstall(context, parse.GetValue(runtimeVersionOption), token), ct));

root.Subcommands.Add(runtimeInstall);

// --------------------------------------------------------------------- init / doctor
root.Subcommands.Add(InitCommand.Build());
root.Subcommands.Add(DoctorCommand.Build());

// --------------------------------------------------------------------------------- db:*
foreach (var command in DbCommands.Build()) root.Subcommands.Add(command);

// ----------------------------------------------------------------------------- invoke:*
foreach (var command in InvokeCommands.Build()) root.Subcommands.Add(command);

// ------------------------------------------------------------------------------- stub:*
root.Subcommands.Add(StubCommands.BuildPublish());
root.Subcommands.Add(StubCommands.BuildDiff());

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

// Top-level guard. An unexpected exception must never reach the user as a raw stack trace:
// forge edits source files, so an opaque crash is exactly when someone needs to know what state
// their tree is in. Exit code 1 per the documented contract.
try
{
    return root.Parse(args).Invoke();
}
catch (Exception ex)
{
    var output = new Output(json: false, noColor: args.Contains("--no-color"));
    output.Failure($"x forge failed unexpectedly: {ex.Message}");
    output.Dim("  No files were written by the failing command - PlanExecutor is all-or-nothing.");
    output.Dim("  Re-run with --verbosity d for the full stack trace, or report this as a bug.");

    if (args.Contains("-v") || args.Contains("--verbosity"))
        output.Dim(ex.ToString());

    return ExitCodes.Error;
}
