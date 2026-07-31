using System.Text;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Roslyn;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

public sealed record ResourceSpec(
    string Entity,
    string? Audience,
    string? View,
    IReadOnlyList<string> Only,
    IReadOnlyList<string> Exclude);

/// <summary>
/// make:resource — an audience-shaped response record derived from the entity, plus its Mapster
/// registration.
///
/// Responses only. In this template the command/query record already IS the inbound contract,
/// so a parallel Request type is ceremony unless the HTTP shape must diverge from the message.
/// </summary>
internal static class ResourceScaffold
{
    public static PlanResult Plan(TemplateContext ctx, ResourceSpec spec)
    {
        var config = ctx.Config;
        var entity = NameHelper.Pascal(spec.Entity);

        if (!NameHelper.IsValidIdentifier(entity))
            return PlanResult.UsageError($"'{spec.Entity}' is not a valid C# identifier.");

        if (spec.Only.Count > 0 && spec.Exclude.Count > 0)
            return PlanResult.UsageError("--only and --exclude are mutually exclusive.");

        var audienceError = Validate(spec.Audience, config.ResourceNaming.Audiences, "audience");
        if (audienceError is not null) return PlanResult.UsageError(audienceError);

        var viewError = Validate(spec.View, config.ResourceNaming.Views, "view");
        if (viewError is not null) return PlanResult.UsageError(viewError);

        // The entity is the source of truth for the response's shape.
        var entityType = ctx.DomainIndex.Types.FirstOrDefault(t =>
            t.Name == entity &&
            t.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.RecordDeclaration);

        if (entityType is null)
        {
            return PlanResult.UsageError(
                $"No entity type '{entity}' found in {config.DomainProject}." + Environment.NewLine +
                $"  -> Create it first:  forge make:entity -n {entity}");
        }

        var selection = SelectProperties(entityType.Properties, spec);
        if (selection.Error is not null) return PlanResult.UsageError(selection.Error);

        if (selection.Properties.Count == 0)
        {
            return PlanResult.UsageError(
                $"'{entity}' has no properties to project onto a response." + Environment.NewLine +
                $"  -> Add properties with make:entity, or widen --only.");
        }

        var responseName = config.ResourceNaming.Compose(
            entity,
            Segment(spec.Audience),
            Segment(spec.View));

        // PLURAL folder, deliberately. A folder named "Order" makes the namespace end in
        // ".Order", and inside it the identifier Order then resolves to the namespace rather
        // than the entity type (CS0118). Pluralising sidesteps that and matches DbSet naming.
        var relativeDir = Path.Combine(config.ApplicationResourcesPath, NameHelper.Plural(entity));
        var resourceNamespace = Namespaces.For(config.ApplicationNamespace, relativeDir);
        var entitiesNamespace = Namespaces.For(config.DomainNamespace, config.DomainEntitiesPath);

        var plan = GenerationPlan.Empty;

        // ---- response record ------------------------------------------------------------
        var responsePath = ctx.PathIn(config.ApplicationProject, relativeDir, $"{responseName}.cs");
        if (File.Exists(responsePath) && !ctx.Force)
        {
            plan = plan.With(new FileAction.Skip(responsePath, "already exists (use --force to overwrite)"));
        }
        else
        {
            plan = plan.With(new FileAction.Create(responsePath, ctx.Render("Response.cs.txt",
                new Dictionary<string, string>
                {
                    ["Usings"] = ctx.Usings("System"),
                    ["Namespace"] = resourceNamespace,
                    ["Response"] = responseName,
                    ["Audience"] = DescribeAudience(spec),
                    ["Parameters"] = RenderParameters(selection.Properties, ctx.CodeStyle.IndentUnit)
                })));
        }

        // ---- Mapster registration: create the file, or append to it ----------------------
        var mappingName = $"{entity}MappingConfig";
        var mappingPath = ctx.PathIn(config.ApplicationProject, relativeDir, $"{mappingName}.cs");
        var registration = $"config.NewConfig<{entity}, {responseName}>();";

        if (!File.Exists(mappingPath))
        {
            var body = ctx.CodeStyle.IndentUnit + ctx.CodeStyle.IndentUnit + registration;
            plan = plan.With(new FileAction.Create(mappingPath, ctx.Render("MappingConfig.cs.txt",
                new Dictionary<string, string>
                {
                    ["Usings"] = ctx.Usings("System"),
                    ["Namespace"] = resourceNamespace,
                    ["DomainEntitiesNamespace"] = entitiesNamespace,
                    ["Entity"] = entity,
                    ["Registration"] = body
                })));

            return PlanResult.Success(plan);
        }

        var before = PlanExecutor.ReadPreservingEncoding(mappingPath, out _);
        var outcome = SyntaxPatcher.AddMapsterRegistration(
            before, mappingName, entity, responseName,
            ctx.CodeStyle.NewLine, ctx.CodeStyle.IndentUnit);

        return outcome switch
        {
            PatchOutcome.Patched patched =>
                PlanResult.Success(plan.With(new FileAction.Patch(mappingPath, before, patched.After))),

            PatchOutcome.AlreadyPresent already =>
                PlanResult.Success(plan.With(new FileAction.Skip(mappingPath, already.Reason))),

            PatchOutcome.Failed failed =>
                PlanResult.AnchorNotFound($"{ctx.Relative(mappingPath)}: {failed.Error}"),

            _ => PlanResult.Fail(Cli.ExitCodes.Error, "Unknown patch outcome.")
        };
    }

    private static (IReadOnlyList<PropertyInfo> Properties, string? Error) SelectProperties(
        IReadOnlyList<PropertyInfo> all,
        ResourceSpec spec)
    {
        if (spec.Only.Count > 0)
        {
            var unknown = spec.Only
                .Where(o => !all.Any(p => p.Name.Equals(o, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (unknown.Count > 0)
            {
                return ([], $"Unknown propert{(unknown.Count == 1 ? "y" : "ies")} " +
                            $"{string.Join(", ", unknown.Select(u => $"'{u}'"))}." + Environment.NewLine +
                            $"  -> Available: {string.Join(", ", all.Select(p => p.Name))}");
            }

            // Preserve the order the user asked for — it becomes the record parameter order.
            return (spec.Only
                .Select(o => all.First(p => p.Name.Equals(o, StringComparison.OrdinalIgnoreCase)))
                .ToList(), null);
        }

        if (spec.Exclude.Count > 0)
        {
            var unknown = spec.Exclude
                .Where(x => !all.Any(p => p.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (unknown.Count > 0)
            {
                return ([], $"Cannot exclude unknown propert{(unknown.Count == 1 ? "y" : "ies")} " +
                            $"{string.Join(", ", unknown.Select(u => $"'{u}'"))}." + Environment.NewLine +
                            $"  -> Available: {string.Join(", ", all.Select(p => p.Name))}");
            }

            return (all.Where(p => !spec.Exclude.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList(), null);
        }

        return (all, null);
    }

    private static string? Validate(string? value, string[] allowed, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalised = NameHelper.Pascal(value!);
        return allowed.Contains(normalised, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"Unknown {label} '{value}'." + Environment.NewLine +
              $"  -> Configured {label}s: {string.Join(", ", allowed)}" + Environment.NewLine +
              $"  -> Add it under \"resourceNaming\" in forge.config.json to allow it.";
    }

    private static string? Segment(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NameHelper.Pascal(value!);

    private static string DescribeAudience(ResourceSpec spec) =>
        (spec.Audience, spec.View) switch
        {
            (null or "", null or "") => "Default response shape.",
            (null or "", var v) => $"{NameHelper.Pascal(v!)} view.",
            (var a, null or "") => $"Shaped for the {NameHelper.Pascal(a!)} audience.",
            var (a, v) => $"{NameHelper.Pascal(v!)} view, shaped for the {NameHelper.Pascal(a!)} audience."
        };

    private static string RenderParameters(IReadOnlyList<PropertyInfo> properties, string indent)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < properties.Count; i++)
        {
            sb.Append('\n').Append(indent)
              .Append(properties[i].Type).Append(' ').Append(properties[i].Name);
            if (i < properties.Count - 1) sb.Append(',');
        }
        return sb.ToString();
    }
}
