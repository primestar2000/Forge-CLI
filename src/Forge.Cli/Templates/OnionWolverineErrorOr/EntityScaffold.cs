using System.Text;
using Forge.Cli.Planning;
using Forge.Cli.Roslyn;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// make:entity — Domain entity + EF Core IEntityTypeConfiguration&lt;T&gt;, and a DbSet wired
/// into the configured DbContext.
/// </summary>
internal static class EntityScaffold
{
    public static PlanResult Plan(TemplateContext ctx, EntitySpec spec)
    {
        if (!NameHelper.IsValidIdentifier(spec.Name))
            return PlanResult.UsageError($"'{spec.Name}' is not a valid C# identifier, so it cannot name an entity.");

        var config = ctx.Config;
        var name = NameHelper.Pascal(spec.Name);
        var plural = NameHelper.Plural(name);

        var entitiesNamespace = Namespaces.For(config.DomainNamespace, config.DomainEntitiesPath);
        var configNamespace = Namespaces.For(config.InfrastructureNamespace, config.InfrastructureConfigurationsPath);

        var plan = GenerationPlan.Empty;

        // ---- 1. Domain entity -----------------------------------------------------------
        var entityPath = ctx.PathIn(config.DomainProject, config.DomainEntitiesPath, $"{name}.cs");
        var entityStub = spec.Encapsulated ? "EntityEncapsulated.cs.txt" : "Entity.cs.txt";

        plan = plan.Concat(ctx.CreateOrSkip(entityPath, () => ctx.Render(entityStub,
            spec.Encapsulated
                ? new Dictionary<string, string>
                {
                    ["Usings"] = ctx.Usings("System"),
                    ["Namespace"] = entitiesNamespace,
                    ["Entity"] = name,
                    ["Properties"] = RenderProperties(spec.Properties, ctx.CodeStyle.IndentUnit, encapsulated: true),
                    ["Constructor"] = RenderConstructor(name, spec.Properties, ctx.CodeStyle.IndentUnit),
                    ["Update"] = RenderUpdate(spec.Properties, ctx.CodeStyle.IndentUnit)
                }
                : new Dictionary<string, string>
                {
                    ["Usings"] = ctx.Usings("System"),
                    ["Namespace"] = entitiesNamespace,
                    ["Entity"] = name,
                    ["Properties"] = RenderProperties(spec.Properties, ctx.CodeStyle.IndentUnit)
                })));

        // ---- 2. EF Core configuration ---------------------------------------------------
        var configPath = ctx.PathIn(config.InfrastructureProject, config.InfrastructureConfigurationsPath, $"{name}Configuration.cs");
        plan = plan.Concat(ctx.CreateOrSkip(configPath, () => ctx.Render("EntityConfiguration.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System"),
                ["Namespace"] = configNamespace,
                ["DomainEntitiesNamespace"] = entitiesNamespace,
                ["Entity"] = name,
                ["EntityPlural"] = plural,
                ["PropertyConfig"] = RenderPropertyConfig(spec.Properties, ctx.CodeStyle.IndentUnit)
            })));

        // ---- 3. DbSet on the DbContext --------------------------------------------------
        if (spec.SkipDbSet) return PlanResult.Success(plan);

        var contextName = config.ResolvedDbContextName;
        var contextEntry = ctx.InfrastructureIndex.FindType(contextName, Microsoft.CodeAnalysis.CSharp.SyntaxKind.ClassDeclaration);

        if (contextEntry is null)
        {
            // Not fatal: the entity and its configuration are still valid, and
            // ApplyConfigurationsFromAssembly will pick the entity up without a DbSet property.
            return PlanResult.Success(plan.With(new FileAction.Skip(
                ctx.PathIn(config.InfrastructureProject),
                $"no class '{contextName}' found - skipped adding DbSet<{name}> (use --skip-dbset to silence)")));
        }

        var before = PlanExecutor.ReadPreservingEncoding(contextEntry.FilePath, out _);
        var outcome = SyntaxPatcher.AddDbSetToContext(
            before, contextName, name, plural, entitiesNamespace,
            ctx.CodeStyle.NewLine, ctx.CodeStyle.IndentUnit);

        return outcome switch
        {
            PatchOutcome.Patched patched =>
                PlanResult.Success(plan.With(new FileAction.Patch(contextEntry.FilePath, before, patched.After))),

            PatchOutcome.AlreadyPresent already =>
                PlanResult.Success(plan.With(new FileAction.Skip(contextEntry.FilePath, already.Reason))),

            PatchOutcome.Failed failed =>
                PlanResult.AnchorNotFound($"{ctx.Relative(contextEntry.FilePath)}: {failed.Error}"),

            _ => PlanResult.Fail(Cli.ExitCodes.Error, "Unknown patch outcome.")
        };
    }

    private static string RenderProperties(
        IReadOnlyList<PropertySpec> properties, string indent, bool encapsulated = false)
    {
        if (properties.Count == 0)
            return $"{indent}// TODO: add properties, or re-run with --properties \"Name:string,...\"";

        var sb = new StringBuilder();
        foreach (var property in properties)
        {
            sb.Append('\n').Append(indent).Append(property.Render(encapsulated));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The public constructor for an encapsulated entity.
    ///
    /// Empty when there are no properties: the generated signature would be parameterless and
    /// collide with the private EF constructor, and the file would not compile.
    ///
    /// Id is deliberately not assigned here. EF Core generates Guid keys on insert by
    /// convention, exactly as it does for the public-setter shape, so leaving it alone keeps
    /// this change purely about encapsulation.
    /// </summary>
    private static string RenderConstructor(string name, IReadOnlyList<PropertySpec> properties, string indent)
    {
        if (properties.Count == 0) return string.Empty;

        // One newline, not two: the stub already ends the private constructor's line, so a
        // second here renders a double blank.
        var sb = new StringBuilder();
        sb.Append('\n')
          .Append(indent).Append("public ").Append(name).Append('(')
          .Append(string.Join(", ", properties.Select(p => p.Parameter)))
          .Append(')').Append('\n')
          .Append(indent).Append('{').Append('\n');

        foreach (var property in properties)
        {
            sb.Append(indent).Append(indent).Append(property.Assignment).Append('\n');
        }

        return sb.Append(indent).Append('}').ToString();
    }

    /// <summary>
    /// A mutator, because private setters without one produce an entity that can be created and
    /// never changed — which makes every update handler impossible to write.
    ///
    /// Updating every property at once is the only thing a generator can honestly infer; split
    /// it into intention-revealing methods (Rename, Reprice, Cancel) as the domain earns them.
    /// </summary>
    private static string RenderUpdate(IReadOnlyList<PropertySpec> properties, string indent)
    {
        if (properties.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append('\n').Append('\n')
          .Append(indent).Append("public void Update(")
          .Append(string.Join(", ", properties.Select(p => p.Parameter)))
          .Append(')').Append('\n')
          .Append(indent).Append('{').Append('\n');

        foreach (var property in properties)
        {
            sb.Append(indent).Append(indent).Append(property.Assignment).Append('\n');
        }

        return sb.Append(indent).Append('}').ToString();
    }

    private static string RenderPropertyConfig(IReadOnlyList<PropertySpec> properties, string indent)
    {
        var body = indent + indent;
        var sb = new StringBuilder();

        foreach (var property in properties)
        {
            // Only emit configuration that is actually meaningful; a wall of no-op Property()
            // calls would be noise the developer has to read past.
            if (property.Type == "string" && !property.IsNullable)
            {
                sb.Append('\n').Append(body)
                  .Append($"builder.Property(x => x.{property.Name}).IsRequired().HasMaxLength(200);");
            }
            else if (property.Type == "decimal")
            {
                sb.Append('\n').Append(body)
                  .Append($"builder.Property(x => x.{property.Name}).HasPrecision(18, 2);");
            }
        }

        return sb.ToString();
    }

}
