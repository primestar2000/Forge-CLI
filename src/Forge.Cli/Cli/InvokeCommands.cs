using System.CommandLine;
using System.Text.Json.Nodes;
using Forge.Cli.Config;

namespace Forge.Cli.Cli;

/// <summary>
/// invoke:* — Tier 2. Drives messages through the user's REAL Wolverine pipeline, out of process
/// via Forge.Runtime.Wolverine. See RuntimeBridge for why this cannot run in forge's own process.
/// </summary>
public static class InvokeCommands
{
    public const string WolverinePackageId = "Pitechy.Forge.Runtime.Wolverine";

    private static readonly Argument<string> MessageArgument =
        new("message") { Description = "Message type name, e.g. CreateGigCommand." };

    private static readonly Option<string> PayloadOption =
        new("--payload") { Description = "JSON payload. Wins over any factory." };

    /// <summary>
    /// Reads the payload from a file instead of the command line.
    ///
    /// Exists because PowerShell and cmd mangle the quotes in inline JSON — a reported
    /// annoyance that forces --% or manual escaping on Windows. A file has no quoting rules at
    /// all, and it is the better option for any payload big enough to be worth keeping.
    /// </summary>
    private static readonly Option<string> PayloadFileOption =
        new("--payload-file")
        {
            Description = "Path to a file containing the JSON payload. Avoids shell quote " +
                          "mangling; use instead of --payload."
        };

    private static readonly Option<string> FactoryOption =
        new("--factory") { Description = "IForgeMessageFactory to build the payload with." };

    private static readonly Option<string> StateOption =
        new("--state") { Description = "Named factory variant, e.g. invalid." };

    private static readonly Option<string> AsRoleOption =
        new("--as-role")
        {
            Description = "Sign in as this role before invoking. Required for role-guarded " +
                          "messages, since a CLI-launched process has nobody signed in."
        };

    private static readonly Option<string> AsOption =
        new("--as")
        {
            Description = "Sign in as a named identity from .forge/identities/<name>.json, " +
                          "for when a role alone is not enough (id, email, tenant, sub-roles)."
        };

    private static readonly Option<bool> NoBuildOption =
        new("--no-build") { Description = "Skip building first. Only when the output is known fresh." };

    private static readonly Option<bool> ProductionOverride =
        new("--i-know-this-is-production")
        { Description = "Required to invoke a handler when the environment is Production." };

    public static IEnumerable<Command> Build()
    {
        yield return BuildList();
        yield return BuildRun();
    }

    private static Command BuildList()
    {
        var command = new Command("invoke:list", "List every message type Wolverine has a handler for.");
        command.Options.Add(NoBuildOption);
        command.WithGlobals();
        command.SetAction(parse => Execute(parse, "invoke:list", "handlers", [], RenderHandlers, destructive: false));
        return command;
    }

    private static Command BuildRun()
    {
        var command = new Command("invoke:run",
            "Run one message through the real Wolverine pipeline and print the result.");

        command.Arguments.Add(MessageArgument);
        foreach (var option in new Option[] { PayloadOption, PayloadFileOption, FactoryOption, StateOption, AsRoleOption, AsOption, NoBuildOption, ProductionOverride })
            command.Options.Add(option);

        command.WithGlobals();
        command.SetAction(parse =>
        {
            var arguments = new List<string> { "--forge-message", parse.GetValue(MessageArgument)! };

            void Add(Option<string> option, string name)
            {
                var value = parse.GetValue(option);
                if (!string.IsNullOrWhiteSpace(value)) { arguments.Add("--forge-" + name); arguments.Add(value!); }
            }

            var payloadFile = parse.GetValue(PayloadFileOption);
            if (!string.IsNullOrWhiteSpace(payloadFile))
            {
                if (!string.IsNullOrWhiteSpace(parse.GetValue(PayloadOption)))
                {
                    return Fail(parse, "invoke:run",
                        "--payload and --payload-file were both given, and they set the same thing." +
                        Environment.NewLine + "  -> Pass only one.");
                }

                var resolved = Path.GetFullPath(payloadFile!);
                if (!File.Exists(resolved))
                {
                    return Fail(parse, "invoke:run", $"Payload file not found: {resolved}");
                }

                string contents;
                try { contents = File.ReadAllText(resolved); }
                catch (Exception ex)
                {
                    return Fail(parse, "invoke:run", $"Could not read {resolved}: {ex.Message}");
                }

                // Validated here so a malformed file fails in milliseconds, rather than after a
                // build and a host start inside the user's application.
                try { System.Text.Json.Nodes.JsonNode.Parse(contents); }
                catch (Exception ex)
                {
                    return Fail(parse, "invoke:run",
                        $"{resolved} is not valid JSON: {ex.Message}");
                }

                arguments.Add("--forge-payload");
                arguments.Add(contents);
            }

            Add(PayloadOption, "payload");
            Add(FactoryOption, "factory");
            Add(StateOption, "state");

            return Execute(parse, "invoke:run", "invoke", arguments, RenderInvocation,
                destructive: true, withIdentity: true);
        });

        return command;
    }

    /// <summary>Usage error, rendered the same way in both human and JSON modes.</summary>
    private static int Fail(ParseResult parse, string commandName, string error)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        if (json) output.Json(Envelope(commandName, ExitCodes.UsageError, null, error));
        else output.Failure($"x {error}");

        return ExitCodes.UsageError;
    }

    private static int Execute(
        ParseResult parse,
        string commandName,
        string verb,
        IReadOnlyList<string> arguments,
        Action<Output, JsonNode?> render,
        bool destructive,
        bool withIdentity = false)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
        if (!loaded.Ok)
        {
            if (json) output.Json(Envelope(commandName, loaded.ExitCode, null, loaded.Error));
            else output.Failure($"x {loaded.Error}");
            return loaded.ExitCode;
        }

        var config = loaded.Config!;
        var root = loaded.SolutionRoot!;

        // invoke:run executes the real pipeline: real DB writes, real external calls. A warning
        // from a different command is not a guard, so refuse outright (exit 6).
        if (destructive && EfTool.IsProduction(out var environment) && !parse.GetValue(ProductionOverride))
        {
            var error = $"Refusing to run {commandName}: environment is '{environment}'.";
            if (json) output.Json(Envelope(commandName, ExitCodes.EnvironmentGuard, null, error));
            else
            {
                output.Failure($"x {error}");
                output.Dim("  -> invoke:run executes the full pipeline, including database writes.");
                output.Dim("  -> If you are certain, pass --i-know-this-is-production.");
            }
            return ExitCodes.EnvironmentGuard;
        }

        if (!RuntimeBridge.IsReferenced(config, root, WolverinePackageId))
        {
            var error = $"{WolverinePackageId} is not referenced by {config.ApiProject}, " +
                        $"so {commandName} is unavailable (Tier 2).";
            if (json) output.Json(Envelope(commandName, ExitCodes.ConfigInvalid, null, error));
            else
            {
                output.Failure($"x {error}");
                output.Dim("  -> forge runtime:install");
                output.Dim("     (adds the packages and wires Program.cs; --dry-run to preview)");
            }
            return ExitCodes.ConfigInvalid;
        }

        // Resolve the identity BEFORE launching: a missing file or malformed JSON should fail in
        // milliseconds, not after a build and a host start.
        if (withIdentity)
        {
            var identity = IdentityResolver.Resolve(root, parse.GetValue(AsOption), parse.GetValue(AsRoleOption));
            if (!identity.Ok)
            {
                if (json) output.Json(Envelope(commandName, ExitCodes.UsageError, null, identity.Error));
                else output.Failure($"x {identity.Error}");
                return ExitCodes.UsageError;
            }

            if (identity.Json is not null)
            {
                arguments = [.. arguments, "--forge-identity", identity.Json];
            }
        }

        if (parse.GetValue(GlobalOptions.DryRun))
        {
            var display = $"dotnet run --project {config.ApiProject} -- --forge:{verb} {string.Join(" ", arguments)}".TrimEnd();
            if (json) output.Json(Envelope(commandName, ExitCodes.Success, display));
            else
            {
                output.Info("Dry run - nothing was executed.");
                output.Dim($"  {display}");
            }
            return ExitCodes.Success;
        }

        var result = RuntimeBridge.Invoke(config, root, verb, arguments,
            parse.GetValue(NoBuildOption), output, verbose: !json);

        if (!result.Ok)
        {
            if (json) output.Json(Envelope(commandName, ExitCodes.Error, null, result.Error));
            else output.Failure($"x {result.Error}");
            return ExitCodes.Error;
        }

        if (json)
        {
            output.Json(result.Payload!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return ExitCodes.Success;
        }

        render(output, result.Data);
        return ExitCodes.Success;
    }

    private static void RenderHandlers(Output output, JsonNode? data)
    {
        if (data?["messages"] is not JsonArray messages || messages.Count == 0)
        {
            output.Warn("Wolverine has no registered handlers.");
            return;
        }

        // Grouped by namespace: a flat list of forty message types is unreadable, and the
        // namespace is how a developer actually navigates to one.
        var grouped = messages.OfType<JsonObject>()
            .GroupBy(m => m["namespace"]?.GetValue<string>() ?? "(global)")
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            output.Info(group.Key);
            foreach (var message in group.OrderBy(m => m["messageType"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                var name = message["messageType"]?.GetValue<string>() ?? "(unknown)";
                var handlers = (message["handlers"] as JsonArray)?
                    .Select(h => h?.GetValue<string>())
                    .Where(h => h is not null)
                    .ToList() ?? [];

                output.Success($"  {name}");
                foreach (var handler in handlers) output.Dim($"      -> {handler}");
            }
            output.Blank();
        }

        output.Info($"{messages.Count} message type(s) with handlers.");
    }

    private static void RenderInvocation(Output output, JsonNode? data)
    {
        if (data is null) { output.Warn("No result was returned."); return; }

        var messageType = data["messageType"]?.GetValue<string>() ?? "(unknown)";
        var milliseconds = data["milliseconds"]?.GetValue<int>() ?? 0;
        var isError = data["isError"]?.GetValue<bool?>();

        output.Blank();
        output.Dim($"payload  {Compact(data["payload"])}");
        output.Blank();

        // Colour follows the result's own IsError flag when it exposes one — ErrorOr<T> and
        // friends — so a handled failure reads as a failure rather than a green success.
        if (isError == true) output.Failure($"x {messageType} returned an error  ({milliseconds}ms)");
        else output.Success($"v {messageType} succeeded  ({milliseconds}ms)");

        output.Blank();
        output.Info(Pretty(data["result"]));
    }

    private static string Compact(JsonNode? node) =>
        node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }) ?? "(none)";

    private static string Pretty(JsonNode? node) =>
        node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "(null)";

    private static string Envelope(string command, int exitCode, string? invocation, string? error = null)
    {
        var diagnostics = new JsonArray();
        if (!string.IsNullOrEmpty(error)) diagnostics.Add(error);

        return new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["schemaVersion"] = JsonOutput.SchemaVersion,
            ["command"] = command,
            ["success"] = exitCode == ExitCodes.Success,
            ["exitCode"] = exitCode,
            ["invocation"] = invocation,
            ["diagnostics"] = diagnostics
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
