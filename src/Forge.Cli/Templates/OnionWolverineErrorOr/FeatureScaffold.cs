using System.Text;
using Forge.Cli.Planning;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// make:feature — command/query record, Wolverine handler, and FluentValidation validator.
///
/// This is the generator where roleGuardStyle actually changes the emitted shape, which is the
/// whole point of the config flag: one tool serving two team conventions without a fork.
/// </summary>
internal static class FeatureScaffold
{
    public static PlanResult Plan(TemplateContext ctx, FeatureSpec spec)
    {
        var config = ctx.Config;

        if (!NameHelper.IsValidIdentifier(NameHelper.Pascal(spec.Name)))
            return PlanResult.UsageError($"'{spec.Name}' is not a valid C# identifier, so it cannot name a feature.");

        if (spec.Anonymous && spec.Roles.Count > 0)
            return PlanResult.UsageError("--anonymous and --roles are mutually exclusive: a message is either role-guarded or it is not.");

        // Secure by default: RoleCheckMiddleware DENIES any message that implements neither
        // marker. Generating such a message would produce a handler that always throws at
        // runtime, so refuse at generation time instead.
        if (!spec.Anonymous && spec.Roles.Count == 0)
        {
            return PlanResult.UsageError(
                $"'{spec.MessageName}' would implement neither role guard, and the template denies " +
                $"such messages by default at runtime." + Environment.NewLine +
                $"  -> Pass --roles <Role...> to allow specific roles, or --anonymous for a genuinely public message.");
        }

        var roleError = ValidateRoles(ctx, spec);
        if (roleError is not null) return PlanResult.UsageError(roleError);

        var message = spec.MessageName;

        // Features/{Group}/{Commands|Queries}/{Name}/ when grouped, else Features/{Name}/.
        var relativeDir = string.IsNullOrWhiteSpace(spec.Group)
            ? Path.Combine(config.ApplicationFeaturesPath, NameHelper.Pascal(spec.Name))
            : Path.Combine(config.ApplicationFeaturesPath, NameHelper.Pascal(spec.Group!), spec.FolderSegment, NameHelper.Pascal(spec.Name));

        var featureNamespace = Namespaces.For(config.ApplicationNamespace, relativeDir);
        var authNamespace = Namespaces.For(config.ApplicationNamespace, "Common/Interfaces/Authentication");
        var persistenceNamespace = Namespaces.For(config.ApplicationNamespace, config.ApplicationRepoPath) + ".Common";
        var errorsNamespace = Namespaces.For(config.ApplicationNamespace, config.ApplicationErrorsPath);
        var domainAuthNamespace = Namespaces.For(config.DomainNamespace, "Common/Authorization");
        var enumsNamespace = Namespaces.For(config.DomainNamespace, "Enums");

        var (guardDeclaration, guardBody) = RenderGuard(ctx, spec);
        var guardUsings = BuildGuardUsings(ctx, spec, domainAuthNamespace, enumsNamespace);

        var returns = string.IsNullOrWhiteSpace(spec.Returns) ? "Success" : NameHelper.Pascal(spec.Returns!);

        // A --returns type must exist and must be imported, or the handler will not compile.
        // Same principle as make:repo's entity guard: fail here rather than emitting broken code.
        var returnsUsing = string.Empty;
        if (returns != "Success")
        {
            var returnsType = ctx.ApplicationIndex.Types.FirstOrDefault(t =>
                t.Name == returns &&
                t.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.RecordDeclaration);

            if (returnsType is null)
            {
                return PlanResult.UsageError(
                    $"No type '{returns}' found in {config.ApplicationProject}." + Environment.NewLine +
                    $"  -> Create it first:  forge make:resource -n <Entity> [--audience ...] [--view ...]" + Environment.NewLine +
                    $"  -> Or omit --returns to get ErrorOr<Success>.");
            }

            if (!string.IsNullOrEmpty(returnsType.Namespace) && returnsType.Namespace != featureNamespace)
                returnsUsing = $"using {returnsType.Namespace};\n";
        }

        var plan = GenerationPlan.Empty;

        // ---- message record -------------------------------------------------------------
        var messagePath = ctx.PathIn(config.ApplicationProject, relativeDir, $"{message}.cs");
        plan = plan.Concat(CreateOrSkip(ctx, messagePath, () => ctx.Render("Message.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System"),
                ["GuardUsings"] = guardUsings,
                ["Namespace"] = featureNamespace,
                ["Message"] = message,
                ["Parameters"] = RenderParameters(spec.Properties, ctx.CodeStyle.IndentUnit),
                ["GuardDeclaration"] = guardDeclaration,
                ["GuardBody"] = guardBody
            })));

        // ---- handler --------------------------------------------------------------------
        var handlerPath = ctx.PathIn(config.ApplicationProject, relativeDir, $"{message}Handler.cs");
        plan = plan.Concat(CreateOrSkip(ctx, handlerPath, () => ctx.Render("MessageHandler.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System", "System.Threading", "System.Threading.Tasks"),
                ["Namespace"] = featureNamespace,
                ["AuthNamespace"] = authNamespace,
                ["PersistenceNamespace"] = persistenceNamespace,
                ["ErrorsNamespace"] = errorsNamespace,
                ["ReturnsUsing"] = returnsUsing,
                ["Message"] = message,
                ["Returns"] = returns,
                ["HandlerBody"] = RenderHandlerBody(spec, returns, ctx.CodeStyle.IndentUnit)
            })));

        // ---- validator ------------------------------------------------------------------
        var validatorPath = ctx.PathIn(config.ApplicationProject, relativeDir, $"{message}Validator.cs");
        plan = plan.Concat(CreateOrSkip(ctx, validatorPath, () => ctx.Render("MessageValidator.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System"),
                ["Namespace"] = featureNamespace,
                ["Message"] = message,
                ["Rules"] = RenderRules(spec.Properties, ctx.CodeStyle.IndentUnit)
            })));

        return PlanResult.Success(plan);
    }

    /// <summary>
    /// Catches "--roles Manager" when the enum has no Manager, listing what is actually valid.
    /// Without this the generated code fails to compile and the user has to work out why.
    /// </summary>
    private static string? ValidateRoles(TemplateContext ctx, FeatureSpec spec)
    {
        if (spec.Roles.Count == 0) return null;

        var roleEnum = ctx.DomainIndex.Types
            .FirstOrDefault(t => t.Name == ctx.Config.RoleEnum && t.Kind == SyntaxKind.EnumDeclaration);

        // If the enum cannot be found, doctor already reports it — don't block generation twice.
        if (roleEnum is null || roleEnum.Members.Count == 0) return null;

        var unknown = spec.Roles
            .Where(r => !roleEnum.Members.Contains(r, StringComparer.Ordinal))
            .ToList();

        if (unknown.Count == 0) return null;

        return $"Unknown role(s) {string.Join(", ", unknown.Select(r => $"'{r}'"))} " +
               $"for enum {ctx.Config.RoleEnum}." + Environment.NewLine +
               $"  -> Valid roles: {string.Join(", ", roleEnum.Members)}";
    }

    private static (string Declaration, string Body) RenderGuard(TemplateContext ctx, FeatureSpec spec)
    {
        var indent = ctx.CodeStyle.IndentUnit;
        var roleEnum = ctx.Config.RoleEnum;
        var singleArray = ctx.Config.RoleGuardStyle == "single-array";

        if (spec.Anonymous)
        {
            var marker = singleArray ? "IAllowRoleCheckBypass" : "IAllowAnonymousRoles";
            return (" : " + marker, "{" + Environment.NewLine + "}");
        }

        var roles = string.Join(", ", spec.Roles.Select(r => $"{roleEnum}.{r}"));

        if (singleArray)
        {
            var body = new StringBuilder("{").Append('\n')
                .Append(indent).Append($"public {roleEnum}[] Roles => [{roles}];").Append('\n')
                .Append('}');
            return (" : IRequireExplicitRoles", body.ToString());
        }

        var subRoleBody = new StringBuilder("{").Append('\n')
            .Append(indent).Append($"public {roleEnum}[] AllowedRoles => [{roles}];").Append('\n')
            .Append('\n')
            .Append(indent).Append("public string[] AllowedSubRoles => [];").Append('\n')
            .Append('}');

        return (" : IRequiresExplicitRoles", subRoleBody.ToString());
    }

    private static string BuildGuardUsings(
        TemplateContext ctx,
        FeatureSpec spec,
        string domainAuthNamespace,
        string enumsNamespace)
    {
        var usings = new List<string> { domainAuthNamespace };
        if (!spec.Anonymous) usings.Add(enumsNamespace);

        var sb = new StringBuilder();
        foreach (var ns in usings.Distinct().OrderBy(n => n, StringComparer.Ordinal))
            sb.Append("using ").Append(ns).Append(';').Append('\n');

        sb.Append('\n');
        return sb.ToString();
    }

    private static string RenderParameters(IReadOnlyList<PropertySpec> properties, string indent)
    {
        if (properties.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < properties.Count; i++)
        {
            sb.Append('\n').Append(indent)
              .Append(properties[i].DeclaredType).Append(' ').Append(properties[i].Name);
            if (i < properties.Count - 1) sb.Append(',');
        }
        return sb.ToString();
    }

    private static string RenderHandlerBody(FeatureSpec spec, string returns, string indent)
    {
        var body = indent + indent;
        var sb = new StringBuilder();

        sb.Append(body).Append("// TODO: implement ").Append(spec.MessageName).Append('\n');

        if (spec.Kind == MessageKind.Command)
        {
            sb.Append('\n').Append(body).Append("await _unitOfWork.SaveChangesAsync(cancellationToken);").Append('\n');
        }
        else
        {
            // A query must still await something or the compiler warns CS1998 on an async method.
            sb.Append('\n').Append(body).Append("await Task.CompletedTask;").Append('\n');
        }

        sb.Append('\n').Append(body);
        sb.Append(returns == "Success"
            ? "return Result.Success;"
            : $"throw new NotImplementedException(\"{spec.MessageName}Handler returns {returns}.\");");

        return sb.ToString();
    }

    private static string RenderRules(IReadOnlyList<PropertySpec> properties, string indent)
    {
        var body = indent + indent;
        var sb = new StringBuilder();

        var required = properties.Where(p => !p.IsNullable && p.Type == "string").ToList();
        if (required.Count == 0)
        {
            sb.Append(body).Append("// TODO: add validation rules.");
            return sb.ToString();
        }

        for (var i = 0; i < required.Count; i++)
        {
            if (i > 0) sb.Append('\n').Append('\n');
            sb.Append(body).Append($"RuleFor(x => x.{required[i].Name}).NotEmpty();");
        }
        return sb.ToString();
    }

    private static GenerationPlan CreateOrSkip(TemplateContext ctx, string path, Func<string> content)
    {
        if (File.Exists(path) && !ctx.Force)
            return GenerationPlan.Of(new FileAction.Skip(path, "already exists (use --force to overwrite)"));

        return GenerationPlan.Of(new FileAction.Create(path, content()));
    }
}
