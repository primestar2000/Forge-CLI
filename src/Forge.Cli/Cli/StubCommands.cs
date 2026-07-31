using System.CommandLine;
using System.Text.Json.Nodes;
using Forge.Cli.Config;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Cli;

public static class StubCommands
{
    private static readonly Option<string[]> OnlyOption =
        new("--only")
        {
            Description = "Limit to these stub names, e.g. --only Repository.cs.txt.",
            AllowMultipleArgumentsPerToken = true
        };

    // ---------------------------------------------------------------------------- publish

    public static Command BuildPublish()
    {
        var command = new Command("stub:publish",
            "Copy built-in stubs into .forge/stubs so generated code can be customised without forking.");
        command.Options.Add(OnlyOption);
        command.WithGlobals();
        command.SetAction((parse, ct) =>
            CommandRunner.RunGenerator(parse, "stub:publish", (_, context, _) =>
                Task.FromResult(StubPublisher.Plan(
                    context.Stubs,
                    parse.GetValue(OnlyOption) ?? [],
                    context.Force)), ct));
        return command;
    }

    // ------------------------------------------------------------------------------- diff

    public static Command BuildDiff()
    {
        var command = new Command("stub:diff",
            "Show how published stubs have drifted from the built-in defaults, three ways.");
        command.Options.Add(OnlyOption);
        command.WithGlobals();
        command.SetAction(RunDiff);
        return command;
    }

    private static int RunDiff(ParseResult parse)
    {
        var json = parse.GetValue(GlobalOptions.Json);
        var verbose = parse.GetValue(GlobalOptions.Verbosity) is "d" or "detailed";
        var output = new Output(json, parse.GetValue(GlobalOptions.NoColor));

        var loaded = ConfigLoader.Load(Directory.GetCurrentDirectory());
        if (!loaded.Ok)
        {
            output.Failure($"x {loaded.Error}");
            return loaded.ExitCode;
        }

        var template = TemplateRegistry.Resolve(loaded.Config!.Template);
        if (template is null)
        {
            output.Failure($"x Unknown template '{loaded.Config.Template}'.");
            return ExitCodes.ConfigInvalid;
        }

        var stubs = new StubRepository(loaded.SolutionRoot!, loaded.Config.StubOverridesPath, template.SnippetFolder);
        var only = parse.GetValue(OnlyOption) ?? [];
        var drifts = StubDiffer.Compare(stubs, only);

        if (json)
        {
            output.Json(RenderJson(stubs, drifts));
            return ExitCodes.Success;
        }

        if (drifts.Count == 0)
        {
            output.Info("No published stubs - generators are using the built-in defaults.");
            output.Dim("  -> forge stub:publish       # copy them here to customise");
            return ExitCodes.Success;
        }

        var publishedAt = ReadPublishedVersion(stubs);
        output.Info($"Published at forge {publishedAt ?? "(unknown - no .forge-version)"}, now running {ForgeVersion.Current}");
        output.Blank();

        var width = drifts.Max(d => d.Name.Length) + 2;

        foreach (var drift in drifts)
        {
            var line = $"  {drift.Name.PadRight(width)}{Describe(drift)}";
            switch (drift.Kind)
            {
                case DriftKind.Unchanged: output.Dim(line); break;
                case DriftKind.LocalOnly: output.Success(line); break;
                case DriftKind.Orphaned: output.Failure(line); break;
                default: output.Warn(line); break;
            }

            if (drift.Overlapping)
                output.Warn($"  {new string(' ', width)}! overlapping edit - review before adopting upstream");

            if (verbose && drift.Kind is DriftKind.Both or DriftKind.UpstreamOnly)
            {
                var (local, upstream) = StubDiffer.Render(stubs, drift.Name);
                output.Blank();
                output.Dim("    --- your changes ---");
                output.Diff(local);
                output.Dim("    --- upstream changes ---");
                output.Diff(upstream);
                output.Blank();
            }
        }

        output.Blank();

        var attention = drifts.Count(d => d.NeedsAttention);
        if (attention == 0)
        {
            output.Success("Nothing to adopt - your stubs are current.");
        }
        else
        {
            output.Warn($"{attention} stub(s) have upstream changes you have not adopted.");
            output.Dim("  -> forge stub:diff --verbosity d      # see the actual hunks");
            output.Dim("  -> forge stub:publish --only <name> --force   # take upstream, discarding your edits");
        }

        return ExitCodes.Success;
    }

    private static string Describe(StubDrift drift) => drift.Kind switch
    {
        DriftKind.Unchanged => "unchanged",
        DriftKind.LocalOnly => $"yours +{drift.LocalAdded} -{drift.LocalRemoved}, upstream unchanged",
        DriftKind.UpstreamOnly => $"upstream +{drift.UpstreamAdded} -{drift.UpstreamRemoved} not adopted",
        DriftKind.Both => $"yours +{drift.LocalAdded} -{drift.LocalRemoved}, upstream +{drift.UpstreamAdded} -{drift.UpstreamRemoved}",
        DriftKind.NoBaseline => $"differs +{drift.LocalAdded} -{drift.LocalRemoved} (no baseline - re-publish for a 3-way diff)",
        _ => "matches no built-in stub - it will never be used"
    };

    private static string? ReadPublishedVersion(StubRepository stubs)
    {
        try
        {
            if (!File.Exists(stubs.VersionMarkerPath)) return null;
            return JsonNode.Parse(File.ReadAllText(stubs.VersionMarkerPath))?["forgeVersion"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private static string RenderJson(StubRepository stubs, IReadOnlyList<StubDrift> drifts)
    {
        var array = new JsonArray();
        foreach (var drift in drifts)
        {
            array.Add(new JsonObject
            {
                ["name"] = drift.Name,
                ["kind"] = drift.Kind.ToString().ToLowerInvariant(),
                ["localAdded"] = drift.LocalAdded,
                ["localRemoved"] = drift.LocalRemoved,
                ["upstreamAdded"] = drift.UpstreamAdded,
                ["upstreamRemoved"] = drift.UpstreamRemoved,
                ["overlapping"] = drift.Overlapping
            });
        }

        return new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["schemaVersion"] = JsonOutput.SchemaVersion,
            ["command"] = "stub:diff",
            ["publishedVersion"] = ReadPublishedVersion(stubs),
            ["needsAttention"] = drifts.Count(d => d.NeedsAttention),
            ["stubs"] = array
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
