using Forge.Cli.Planning;
using Forge.Cli.Roslyn;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// Onion / Clean Architecture + Wolverine + ErrorOr, with a UnitOfWork facade over
/// per-aggregate repositories.
/// </summary>
public sealed class OnionWolverineErrorOrTemplate : ITemplate
{
    public string Name => "onion-wolverine-erroror";

    public string SnippetFolder => "OnionWolverineErrorOr";

    public Task<PlanResult> PlanRepository(TemplateContext ctx, string entity, CancellationToken ct)
    {
        // Validate input BEFORE building any part of the plan, so bad input is a clean usage
        // error rather than a confusing failure halfway through.
        if (!NameHelper.IsValidIdentifier(entity))
            return Task.FromResult(PlanResult.UsageError(
                $"'{entity}' is not a valid C# identifier, so it cannot name an entity."));

        var config = ctx.Config;
        var name = NameHelper.Pascal(entity);
        var plan = GenerationPlan.Empty;

        var repoInterface = NameHelper.RepositoryInterface(name);
        var appRepoNamespace = Namespaces.For(config.ApplicationNamespace, config.ApplicationRepoPath);
        var infraRepoNamespace = Namespaces.For(config.InfrastructureNamespace, config.InfrastructureRepoPath);
        var errorsNamespace = Namespaces.For(config.ApplicationNamespace, config.ApplicationErrorsPath);

        // ---- 1. I{Entity}Repository ------------------------------------------------------
        var interfacePath = ctx.PathIn(config.ApplicationProject, config.ApplicationRepoPath, $"{repoInterface}.cs");
        plan = plan.Concat(CreateOrSkip(ctx, interfacePath, () => ctx.Render("RepositoryInterface.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks"),
                ["Namespace"] = appRepoNamespace,
                ["DomainNamespace"] = config.DomainNamespace,
                ["Entity"] = name,
                ["EntityCamel"] = NameHelper.Camel(name),
                ["Nullable"] = ctx.NullableMarker
            })));

        // ---- 2. {Entity}Repository -------------------------------------------------------
        var classPath = ctx.PathIn(config.InfrastructureProject, config.InfrastructureRepoPath, $"{NameHelper.RepositoryClass(name)}.cs");
        plan = plan.Concat(CreateOrSkip(ctx, classPath, () => ctx.Render("Repository.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System", "System.Collections.Generic", "System.Linq", "System.Threading", "System.Threading.Tasks"),
                ["Namespace"] = infraRepoNamespace,
                ["ApplicationRepoNamespace"] = appRepoNamespace,
                ["DomainNamespace"] = config.DomainNamespace,
                ["ApplicationNamespace"] = config.ApplicationNamespace,
                ["Entity"] = name,
                ["EntityCamel"] = NameHelper.Camel(name),
                ["EntityPlural"] = NameHelper.Plural(name),
                ["Nullable"] = ctx.NullableMarker,
                ["DbContextName"] = config.ResolvedDbContextName
            })));

        // ---- 3. Errors.{Entity}.cs -------------------------------------------------------
        var errorsPath = ctx.PathIn(config.ApplicationProject, config.ApplicationErrorsPath, $"Errors.{name}.cs");
        plan = plan.Concat(CreateOrSkip(ctx, errorsPath, () => ctx.Render("Errors.cs.txt",
            new Dictionary<string, string>
            {
                ["Usings"] = ctx.Usings("System"),
                ["Namespace"] = errorsNamespace,
                ["Entity"] = name,
                ["EntityCamelSpaced"] = NameHelper.Camel(name)
            })));

        // ---- 4. IUnitOfWork.cs -----------------------------------------------------------
        var interfaceFile = ctx.PathIn(config.ApplicationProject, config.ApplicationUnitOfWorkInterfacePath);
        var uowInterface = PatchFile(ctx, interfaceFile, "applicationUnitOfWorkInterfacePath", source =>
            SyntaxPatcher.AddRepositoryToInterface(
                source, "IUnitOfWork", name, repoInterface,
                ctx.CodeStyle.NewLine, ctx.CodeStyle.IndentUnit));

        if (!uowInterface.Ok) return Task.FromResult(uowInterface);
        plan = plan.Concat(uowInterface.Plan);

        // ---- 5. UnitOfWork.cs ------------------------------------------------------------
        var implFile = ctx.PathIn(config.InfrastructureProject, config.InfrastructureUnitOfWorkImplPath);
        var uowImpl = PatchFile(ctx, implFile, "infrastructureUnitOfWorkImplPath", source =>
            SyntaxPatcher.AddRepositoryToUnitOfWork(
                source, "UnitOfWork", name, repoInterface,
                NameHelper.RepositoryParameter(name),
                ctx.CodeStyle.NewLine, ctx.CodeStyle.IndentUnit));

        if (!uowImpl.Ok) return Task.FromResult(uowImpl);
        plan = plan.Concat(uowImpl.Plan);

        return Task.FromResult(PlanResult.Success(plan));
    }

    /// <summary>
    /// The drift checks that matter for this architecture: config claiming one role-guard shape
    /// while the codebase actually contains the other is the exact failure mode doctor exists for.
    /// </summary>
    public IEnumerable<Diagnostics.CheckResult> Diagnose(Diagnostics.DoctorContext ctx)
    {
        if (!ctx.HasConfig)
        {
            yield return Diagnostics.CheckResult.Skip("role guard", "no config");
            yield break;
        }

        var config = ctx.Config!;
        var index = ctx.Index;

        // ---- role guard shape vs. reality
        var singleArray = index.FindType("IRequireExplicitRoles", SyntaxKind.InterfaceDeclaration) is not null;
        var roleAndSub = index.FindType("IRequiresExplicitRoles", SyntaxKind.InterfaceDeclaration) is not null;
        var expected = config.RoleGuardStyle;

        if (!singleArray && !roleAndSub)
        {
            yield return Diagnostics.CheckResult.Warn("role guard",
                $"config says '{expected}' but neither marker interface exists in the solution",
                "Generated features will reference a type that isn't there. Add the marker interface, " +
                "or run 'forge make:solution' ideology files.");
        }
        else
        {
            var actual = singleArray ? "single-array" : "role-and-subrole";
            yield return actual == expected
                ? Diagnostics.CheckResult.Pass("role guard", $"'{expected}' matches the codebase")
                : Diagnostics.CheckResult.Fail("role guard",
                    $"config says '{expected}' but the codebase contains " +
                    $"{(singleArray ? "IRequireExplicitRoles" : "IRequiresExplicitRoles")} ('{actual}')",
                    $"Set \"roleGuardStyle\": \"{actual}\" in forge.config.json");
        }

        // ---- role enum
        var roleEnum = index.FindType(config.RoleEnum, SyntaxKind.EnumDeclaration);
        yield return roleEnum is not null
            ? Diagnostics.CheckResult.Pass("role enum", $"enum {config.RoleEnum}")
            : Diagnostics.CheckResult.Fail("role enum",
                $"config names '{config.RoleEnum}' but no such enum exists in the solution",
                "Fix \"roleEnum\" in forge.config.json, or re-run 'forge init --force'.");

        // ---- DbContext (repositories are generated against it)
        var dbContext = index.FindType(config.ResolvedDbContextName, SyntaxKind.ClassDeclaration);
        yield return dbContext is not null
            ? Diagnostics.CheckResult.Pass("DbContext", config.ResolvedDbContextName)
            : Diagnostics.CheckResult.Fail("DbContext",
                $"config names '{config.ResolvedDbContextName}' but no such class exists - generated repositories will not compile",
                "Fix \"dbContextName\" in forge.config.json, or re-run 'forge init --force'.");

        // ---- scheduler package actually referenced
        var (package, label) = config.Scheduler switch
        {
            "hangfire" => ("Hangfire", "Hangfire"),
            "quartz" => ("Quartz", "Quartz"),
            _ => ("WolverineFx", "Wolverine")
        };

        yield return ctx.AllProjectFiles.Contains(package, StringComparison.OrdinalIgnoreCase)
            ? Diagnostics.CheckResult.Pass("scheduler", $"{config.Scheduler} ({label} referenced)")
            : Diagnostics.CheckResult.Warn("scheduler",
                $"config says '{config.Scheduler}' but no {label} package reference was found",
                $"make:job output will not compile until {package} is referenced.");
    }

    private static GenerationPlan CreateOrSkip(TemplateContext ctx, string path, Func<string> content)
    {
        if (File.Exists(path) && !ctx.Force)
            return GenerationPlan.Of(new FileAction.Skip(path, "already exists (use --force to overwrite)"));

        return GenerationPlan.Of(new FileAction.Create(path, content()));
    }

    private static PlanResult PatchFile(
        TemplateContext ctx,
        string path,
        string configKey,
        Func<string, PatchOutcome> patch)
    {
        if (!File.Exists(path))
        {
            return PlanResult.ConfigInvalid(
                $"Could not find {ctx.Relative(path)}." + Environment.NewLine +
                $"  -> Check '{configKey}' in forge.config.json, or run 'forge config:validate'.");
        }

        var before = PlanExecutor.ReadPreservingEncoding(path, out _);

        return patch(before) switch
        {
            PatchOutcome.Patched patched =>
                PlanResult.Success(GenerationPlan.Of(new FileAction.Patch(path, before, patched.After))),

            PatchOutcome.AlreadyPresent already =>
                PlanResult.Success(GenerationPlan.Of(new FileAction.Skip(path, already.Reason))),

            PatchOutcome.Failed failed =>
                PlanResult.AnchorNotFound($"{ctx.Relative(path)}: {failed.Error}"),

            _ => PlanResult.Fail(Cli.ExitCodes.Error, "Unknown patch outcome.")
        };
    }
}

internal static class Namespaces
{
    /// <summary>"Common/Interfaces/Persistence" -> "{root}.Common.Interfaces.Persistence".</summary>
    public static string For(string rootNamespace, string relativePath)
    {
        var suffix = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".");

        var parts = new[] { rootNamespace }.Concat(suffix).Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join('.', parts);
    }
}
