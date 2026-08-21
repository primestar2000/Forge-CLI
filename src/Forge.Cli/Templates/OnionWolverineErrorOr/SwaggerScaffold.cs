using Forge.Cli.Planning;
using Forge.Cli.Roslyn;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// Swagger / OpenAPI wiring, shared by both surfaces that need it.
///
/// <c>make:solution</c> renders these strings into Program.cs and the API csproj at generation
/// time; <c>swagger:install</c> patches the same content into a solution that already exists.
/// One source of truth, because two copies of "what forge considers correct Swagger wiring"
/// drift the moment either is touched.
///
/// Swashbuckle rather than the .NET 9+ built-in AddOpenApi: <c>make:solution -f</c> can target
/// net8.0, where AddOpenApi does not exist, and Swashbuckle carries the UI in the same package
/// instead of needing a second one.
/// </summary>
public static class SwaggerScaffold
{
    public const string Package = "Swashbuckle.AspNetCore";

    /// <summary>
    /// 9.0.6 ships net8.0 and net9.0 assets, so it resolves for every framework
    /// <c>make:solution -f</c> can produce — net8.0 takes the net8.0 asset, net10.0 rolls
    /// forward to the net9.0 one.
    /// </summary>
    public const string Version = "9.0.6";

    /// <summary>
    /// ApiExplorer is what feeds the document from the attribute-routed controllers. Without it
    /// Swashbuckle still generates a document, but an empty one — which reads as "Swagger is
    /// broken" rather than "a service is missing".
    /// </summary>
    public static readonly (string Marker, string Statement, string Description)[] Registrations =
    [
        ("AddEndpointsApiExplorer(", "builder.Services.AddEndpointsApiExplorer();", "AddEndpointsApiExplorer()"),
        ("AddSwaggerGen(", "builder.Services.AddSwaggerGen();", "AddSwaggerGen()")
    ];

    /// <summary>
    /// Development only. The document describes every endpoint, including ones behind role
    /// guards, and publishing that by default would be forge making an exposure decision on the
    /// developer's behalf.
    /// </summary>
    public static string Middleware(string indent, string newLine) =>
        "// Development only: the document lists every endpoint, including role-guarded ones." + newLine +
        "if (app.Environment.IsDevelopment())" + newLine +
        "{" + newLine +
        indent + "app.UseSwagger();" + newLine +
        indent + "app.UseSwaggerUI();" + newLine +
        "}";

    public static string PackageReferenceXml(string indent, string newLine) =>
        indent + "<!-- OpenAPI document + /swagger UI. See swagger:install to add this later. -->" + newLine +
        indent + $"<PackageReference Include=\"{Package}\" Version=\"{Version}\" />";

    /// <summary>
    /// Wires Swagger into a solution that already exists.
    ///
    /// Mirrors RuntimeInstallScaffold deliberately: same anchors, same additive edits, same
    /// per-statement idempotency markers. A shared marker for the whole batch would skip the
    /// second statement on any solution that already has the first — and forge's own template
    /// has always emitted AddEndpointsApiExplorer(), so that case is the common one, not an edge.
    /// </summary>
    public static PlanResult Plan(TemplateContext ctx)
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
                $"No .csproj found in '{ctx.Config.ApiProject}', so forge cannot add {Package}." +
                Environment.NewLine +
                "  -> Check the apiProject path in forge.config.json.");
        }

        var programPath = Path.Combine(apiDirectory, "Program.cs");
        if (!File.Exists(programPath))
        {
            return PlanResult.AnchorNotFound(
                $"No Program.cs in '{ctx.Config.ApiProject}'. forge needs it to register Swagger." +
                Environment.NewLine +
                "  -> Add builder.Services.AddSwaggerGen() and app.UseSwagger() by hand.");
        }

        var program = File.ReadAllText(programPath);
        var newLine = ctx.CodeStyle.NewLine;
        var indent = ctx.CodeStyle.IndentUnit;

        var plan = GenerationPlan.Empty;

        // ---- package --------------------------------------------------------------------
        var csprojText = File.ReadAllText(csproj);
        var csprojOutcome = ProjectFilePatcher.AddPackageReference(
            csprojText, Package, Version, newLine, indent);

        var csprojAfter = csprojText;
        var csprojReason = string.Empty;

        switch (csprojOutcome)
        {
            case PatchOutcome.Patched patched: csprojAfter = patched.After; break;
            case PatchOutcome.AlreadyPresent present: csprojReason = present.Reason; break;
            case PatchOutcome.Failed failed: return PlanResult.AnchorNotFound(failed.Error);
        }

        plan = csprojAfter != csprojText
            ? plan.With(new FileAction.Patch(csproj, csprojText, csprojAfter))
            : plan.With(new FileAction.Skip(csproj, csprojReason));

        // ---- Program.cs -----------------------------------------------------------------
        var programAfter = program;
        var reasons = new List<string>();

        // ProgramPatcher describes the anchor it could not find, but not which file it looked in
        // — and "could not find a top-level statement containing 'builder.Build()'" is unusable
        // without that. Every failure below is reported against the path.
        var relativeProgram = Path.GetRelativePath(ctx.SolutionRoot, programPath).Replace('\\', '/');
        string Located(string error) => $"In {relativeProgram}:{Environment.NewLine}{error}";

        // Only when the project cannot rely on implicit usings. The Web SDK provides both
        // Microsoft.AspNetCore.Builder and Microsoft.Extensions.DependencyInjection, so adding
        // them there would be noise; a brownfield project with ImplicitUsings off genuinely
        // needs them, and omitting one costs a build error the user has to diagnose.
        if (!ctx.CodeStyle.ImplicitUsings)
        {
            if (!Step(ProgramPatcher.EnsureUsings(
                        programAfter,
                        ["Microsoft.AspNetCore.Builder", "Microsoft.Extensions.DependencyInjection"],
                        newLine),
                    ref programAfter, reasons, out var usingError))
            {
                return PlanResult.AnchorNotFound(Located(usingError!));
            }
        }

        foreach (var (marker, statement, description) in Registrations)
        {
            if (!Step(ProgramPatcher.InsertBeforeStatement(
                        programAfter, "builder.Build()", marker, [statement], newLine, description),
                    ref programAfter, reasons, out var registrationError))
            {
                return PlanResult.AnchorNotFound(Located(registrationError!));
            }
        }

        // Middleware goes before the endpoints are mapped, which is where a reader expects it.
        // Anchors are tried in order because not every Program.cs has all of them: a minimal-API
        // app has no MapControllers, and a trimmed one may have dropped UseHttpsRedirection.
        // app.Run() is the backstop — a web application that does not call it does not start.
        var middlewareOutcome = InsertBeforeFirst(
            programAfter,
            ["app.UseHttpsRedirection()", "app.MapControllers()", "app.Run()"],
            "UseSwaggerUI(",
            [Middleware(indent, newLine)],
            newLine,
            "the Swagger middleware");

        if (!Step(middlewareOutcome, ref programAfter, reasons, out var middlewareError))
        {
            return PlanResult.AnchorNotFound(Located(middlewareError!));
        }

        plan = programAfter != program
            ? plan.With(new FileAction.Patch(programPath, program, programAfter))
            : plan.With(new FileAction.Skip(programPath, string.Join("; ", reasons)));

        return PlanResult.Success(plan);
    }

    /// <summary>
    /// Tries each anchor in turn and takes the first that exists. A Failed outcome only means
    /// "this anchor is not here", so it is not an error until every candidate has been tried —
    /// and the error then names all of them rather than only the last one attempted.
    /// </summary>
    private static PatchOutcome InsertBeforeFirst(
        string source,
        IReadOnlyList<string> anchors,
        string idempotencyMarker,
        IReadOnlyList<string> statements,
        string newLine,
        string description)
    {
        foreach (var anchor in anchors)
        {
            var outcome = ProgramPatcher.InsertBeforeStatement(
                source, anchor, idempotencyMarker, statements, newLine, description);

            if (outcome is not PatchOutcome.Failed) return outcome;
        }

        return new PatchOutcome.Failed(
            $"Could not find anywhere to place {description}." + Environment.NewLine +
            $"  Expected a top-level statement containing one of: {string.Join(", ", anchors)}." +
            Environment.NewLine +
            "  -> Add app.UseSwagger() and app.UseSwaggerUI() by hand, or run with --dry-run " +
            "to see the intended change.");
    }

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
