using Forge.Cli.Planning;

namespace Forge.Cli.Templates;

/// <summary>
/// The one seam that knows about a specific architecture. Program.cs, ConfigLoader,
/// PlanExecutor and the diagnostics must contain no onion/Wolverine/ErrorOr knowledge —
/// if any of them mentions Wolverine, that is a layering violation.
///
/// Methods return a plan and never touch disk. A template that cannot support a verb returns
/// GenerationPlan.Empty with a Skip explaining why, rather than throwing
/// NotImplementedException — an unsupported verb should not crash the tool.
/// </summary>
public interface ITemplate
{
    /// <summary>Value of the "template" field in forge.config.json.</summary>
    string Name { get; }

    /// <summary>
    /// Folder under Templates/ holding this template's Snippets. Declared explicitly rather
    /// than derived from <see cref="Name"/> — "onion-wolverine-erroror" would casing-mangle to
    /// "OnionWolverineErroror" and silently miss every embedded resource.
    /// </summary>
    string SnippetFolder { get; }

    Task<PlanResult> PlanEntity(TemplateContext ctx, EntitySpec spec, CancellationToken ct);

    Task<PlanResult> PlanEnum(TemplateContext ctx, EnumSpec spec, CancellationToken ct);

    Task<PlanResult> PlanRepository(TemplateContext ctx, string entity, CancellationToken ct);

    Task<PlanResult> PlanFeature(TemplateContext ctx, FeatureSpec spec, CancellationToken ct);

    Task<PlanResult> PlanResource(TemplateContext ctx, OnionWolverineErrorOr.ResourceSpec spec, CancellationToken ct);

    /// <summary>
    /// One-time solution scaffold. Takes a SolutionSpec rather than TemplateContext because
    /// there is no forge.config.json yet — this command creates it.
    /// </summary>
    Task<PlanResult> PlanSolution(SolutionScaffoldContext ctx, SolutionSpec spec, CancellationToken ct);

    /// <summary>
    /// Wires the tier-2 runtime into a solution that already exists — packages, usings, the
    /// hook and any registrations the architecture needs.
    ///
    /// Separate from PlanSolution because the need is discovered after scaffolding: a
    /// creation-time flag cannot help a solution that already exists, which is where every
    /// report of "invoke:* is unavailable" has come from.
    /// </summary>
    Task<PlanResult> PlanRuntimeInstall(TemplateContext ctx, string? version, CancellationToken ct);

    /// <summary>
    /// Wires an OpenAPI document and its UI into a solution that already exists.
    ///
    /// Separate from PlanSolution for the same reason PlanRuntimeInstall is: solutions
    /// scaffolded before this existed, and brownfield ones forge never created, both need the
    /// wiring retrofitted rather than generated.
    /// </summary>
    Task<PlanResult> PlanSwaggerInstall(TemplateContext ctx, CancellationToken ct);

    /// <summary>
    /// Architecture-specific health checks (role-guard shape, scheduler package, DbContext).
    /// These live here rather than in Diagnostics/Checks so the core diagnostics stay free of
    /// onion/Wolverine/ErrorOr knowledge — invariant 8.
    /// </summary>
    IEnumerable<Diagnostics.CheckResult> Diagnose(Diagnostics.DoctorContext ctx);
}

public static class TemplateRegistry
{
    private static readonly Dictionary<string, Func<ITemplate>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["onion-wolverine-erroror"] = () => new OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
    };

    public static IReadOnlyCollection<string> Names => Factories.Keys;

    public static ITemplate? Resolve(string name) =>
        Factories.TryGetValue(name, out var factory) ? factory() : null;
}
