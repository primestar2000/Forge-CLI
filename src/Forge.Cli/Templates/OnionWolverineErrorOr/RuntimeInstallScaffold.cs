using Forge.Cli.Cli;
using Forge.Cli.Planning;
using Forge.Cli.Roslyn;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// Wires tier 2 into a solution that already exists.
///
/// make:solution --with-runtime only helps at creation time, and the real workflow discovers it
/// wants invoke:* long after that — three separate projects hit exactly this and each needed the
/// same four hand edits. This performs them.
///
/// Every edit is additive: package references are appended, statements are inserted, and the
/// existing scoped CurrentUser registrations are left in place rather than rewritten. Nothing
/// here replaces a line the developer wrote, so re-running is a no-op and a hand-wired solution
/// is recognised as already done.
/// </summary>
public static class RuntimeInstallScaffold
{
    public const string CorePackage = "Pitechy.Forge.Runtime";
    public const string WolverinePackage = "Pitechy.Forge.Runtime.Wolverine";

    public static PlanResult Plan(TemplateContext ctx, string? versionOverride)
    {
        var apiDirectory = Path.Combine(ctx.SolutionRoot, ctx.Config.ApiProject);
        if (!Directory.Exists(apiDirectory))
        {
            return PlanResult.ConfigInvalid(
                $"The API project directory '{ctx.Config.ApiProject}' does not exist under {ctx.SolutionRoot}." +
                Environment.NewLine +
                "  -> Check the apiProject path in forge.config.json, or run 'forge doctor'.");
        }

        var csproj = Directory
            .EnumerateFiles(apiDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (csproj is null)
        {
            return PlanResult.ConfigInvalid(
                $"No .csproj found in '{ctx.Config.ApiProject}', so forge cannot add the runtime packages." +
                Environment.NewLine +
                "  -> Check the apiProject path in forge.config.json.");
        }

        var programPath = Path.Combine(apiDirectory, "Program.cs");
        if (!File.Exists(programPath))
        {
            return PlanResult.AnchorNotFound(
                $"No Program.cs in '{ctx.Config.ApiProject}'. forge needs it to place the runtime hook." +
                Environment.NewLine +
                $"  -> Add this before app.Run() by hand:{Environment.NewLine}" +
                "       if (await app.RunForgeRuntimeAsync(args)) return;");
        }

        var program = File.ReadAllText(programPath);

        // Only wire the Wolverine half into an application that actually uses Wolverine.
        // Emitting AddForgeWolverine() into a project without the bus would not compile, which
        // is a far worse outcome than telling the user invoke:* is not applicable.
        var usesWolverine = program.Contains("UseWolverine", StringComparison.Ordinal);

        // The singleton identity override only makes sense where these types exist. In a
        // brownfield solution forge did not scaffold, they may not — and referencing them would
        // break the build.
        var hasIdentity = Declares(ctx, "ICurrentUserSetter") && Declares(ctx, "CurrentUser");

        var version = versionOverride ?? ForgeVersion.Current;
        var newLine = ctx.CodeStyle.NewLine;

        var plan = GenerationPlan.Empty;

        // ---- packages -------------------------------------------------------------------
        var packages = usesWolverine ? new[] { CorePackage, WolverinePackage } : [CorePackage];
        var csprojText = File.ReadAllText(csproj);
        var csprojAfter = csprojText;
        var csprojReasons = new List<string>();

        foreach (var package in packages)
        {
            var outcome = ProjectFilePatcher.AddPackageReference(
                csprojAfter, package, version, newLine, ctx.CodeStyle.IndentUnit);

            switch (outcome)
            {
                case PatchOutcome.Patched patched: csprojAfter = patched.After; break;
                case PatchOutcome.AlreadyPresent present: csprojReasons.Add(present.Reason); break;
                case PatchOutcome.Failed failed: return PlanResult.AnchorNotFound(failed.Error);
            }
        }

        plan = csprojAfter != csprojText
            ? plan.With(new FileAction.Patch(csproj, csprojText, csprojAfter))
            : plan.With(new FileAction.Skip(csproj, string.Join("; ", csprojReasons)));

        // ---- Program.cs -----------------------------------------------------------------
        var programAfter = program;
        var programReasons = new List<string>();

        var namespaces = usesWolverine
            ? new[] { "Forge.Runtime", "Forge.Runtime.Wolverine" }
            : ["Forge.Runtime"];

        if (!Step(ProgramPatcher.EnsureUsings(programAfter, namespaces, newLine),
                ref programAfter, programReasons, out var error))
        {
            return PlanResult.AnchorNotFound(error!);
        }

        // Registrations go immediately before the host is built — after everything the
        // developer registered, which is what makes the identity override take precedence.
        //
        // Each carries its OWN idempotency marker rather than sharing one for the batch. A
        // shared marker duplicates the identity block on any solution that already has it but
        // lacks AddForgeWolverine — a state that arises naturally from scaffolding with the
        // older --with-runtime and then running this command.
        var registrations = new List<(string Marker, string Statement)>();

        if (usesWolverine)
        {
            registrations.Add(("AddForgeWolverine(",
                "// Registers the invoke:list and invoke:run verb handlers. Adds no behaviour to a" + newLine +
                "// normal start - RunForgeRuntimeAsync only dispatches when forge launched this" + newLine +
                "// process." + newLine +
                "builder.Services.AddForgeWolverine();"));
        }

        if (hasIdentity)
        {
            // Deliberately an override rather than an edit to the existing registrations.
            // Wolverine opens a scope per message, so a scoped identity set by the runtime is
            // not the instance the handler resolves and every guarded message is denied. The
            // last registration wins, so appending is enough and the developer's own lines are
            // left untouched.
            registrations.Add(("IsForgeInvocation(args)",
                "if (ForgeRuntimeExtensions.IsForgeInvocation(args))" + newLine +
                "{" + newLine +
                ctx.CodeStyle.IndentUnit + "// forge invocation: one message, one process, so one identity for its" + newLine +
                ctx.CodeStyle.IndentUnit + "// lifetime. Wolverine scopes each message, and a scoped identity would" + newLine +
                ctx.CodeStyle.IndentUnit + "// not be the instance the handler sees." + newLine +
                ctx.CodeStyle.IndentUnit + "builder.Services.AddSingleton<CurrentUser>();" + newLine +
                ctx.CodeStyle.IndentUnit + "builder.Services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());" + newLine +
                ctx.CodeStyle.IndentUnit + "builder.Services.AddSingleton<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());" + newLine +
                "}"));
        }

        foreach (var (marker, statement) in registrations)
        {
            if (!Step(ProgramPatcher.InsertBeforeStatement(
                        programAfter, "builder.Build()", marker, [statement], newLine,
                        "the forge runtime registrations"),
                    ref programAfter, programReasons, out error))
            {
                return PlanResult.AnchorNotFound(error!);
            }
        }

        if (!Step(ProgramPatcher.InsertAfterStatement(
                    programAfter, "builder.Build()", "RunForgeRuntimeAsync",
                    [
                        "// forge hook. No-op on a normal start; short-circuits only when forge launched" + newLine +
                        "// this process with a --forge:<verb> argument, so the web server never binds a" + newLine +
                        "// port." + newLine +
                        "if (await app.RunForgeRuntimeAsync(args)) return;"
                    ],
                    newLine, "the forge runtime hook"),
                ref programAfter, programReasons, out error))
        {
            return PlanResult.AnchorNotFound(error!);
        }

        plan = programAfter != program
            ? plan.With(new FileAction.Patch(programPath, program, programAfter))
            : plan.With(new FileAction.Skip(programPath, string.Join("; ", programReasons)));

        return PlanResult.Success(plan);
    }

    private static bool Declares(TemplateContext ctx, string typeName) =>
        ctx.ApplicationIndex.Types.Any(t => t.Name == typeName);

    /// <summary>
    /// Threads one patch outcome into the running text. Returns false only for a real failure —
    /// AlreadyPresent is the idempotent path and leaves the text as it was.
    /// </summary>
    private static bool Step(
        PatchOutcome outcome, ref string text, List<string> reasons, out string? error)
    {
        error = null;
        switch (outcome)
        {
            case PatchOutcome.Patched patched:
                text = patched.After;
                return true;
            case PatchOutcome.AlreadyPresent present:
                reasons.Add(present.Reason);
                return true;
            case PatchOutcome.Failed failed:
                error = failed.Error;
                return false;
            default:
                error = "Unknown patch outcome.";
                return false;
        }
    }
}
