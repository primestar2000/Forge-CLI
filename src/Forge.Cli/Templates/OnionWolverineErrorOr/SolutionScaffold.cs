using System.Text.Json;
using Forge.Cli.Cli;
using Forge.Cli.Config;
using Forge.Cli.Planning;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// make:solution for the onion / Wolverine / ErrorOr template.
///
/// Generates every file itself rather than shelling out to `dotnet new` + `dotnet sln add`.
/// Shelling out would write files outside the GenerationPlan, which would break --dry-run
/// (it could no longer preview anything), break command-level atomicity, and make the command
/// non-deterministic. Project GUIDs are derived from project names for the same reason.
/// </summary>
internal static class SolutionScaffold
{
    public static PlanResult Plan(SolutionScaffoldContext ctx, SolutionSpec spec)
    {
        if (!NameHelper.IsValidIdentifier(spec.Name))
            return PlanResult.UsageError($"'{spec.Name}' is not a valid C# identifier, so it cannot name a solution.");

        if (spec.RoleGuardStyle is not ("single-array" or "role-and-subrole"))
            return PlanResult.UsageError($"--role-guard must be 'single-array' or 'role-and-subrole', not '{spec.RoleGuardStyle}'.");

        var s = spec.Name;
        var plan = GenerationPlan.Empty;

        // ---- namespaces -----------------------------------------------------------------
        var domainNs = $"{s}.Domain";
        var appNs = $"{s}.ApplicationService";
        var infraNs = $"{s}.Infrastructure";
        var apiNs = $"{s}.API";

        var enumsNs = $"{domainNs}.Enums";
        var domainAuthNs = $"{domainNs}.Common.Authorization";
        var appAuthNs = $"{appNs}.Common.Interfaces.Authentication";
        var appPersistenceNs = $"{appNs}.Common.Interfaces.Persistence.Common";
        var appErrorsNs = $"{appNs}.Common.Errors";
        var appResponseNs = $"{appNs}.Common.Response";
        var infraPersistenceNs = $"{infraNs}.Persistence.Repository.Common";
        var dbContextNs = $"{infraNs}.Persistence";
        var apiMiddlewareNs = $"{apiNs}.Middleware";

        var domainDir = Path.Combine("src", $"{s}.Domain");
        var appDir = Path.Combine("src", $"{s}.ApplicationService");
        var infraDir = Path.Combine("src", $"{s}.Infrastructure");
        var apiDir = Path.Combine("src", $"{s}.API");

        GenerationPlan Add(GenerationPlan p, string relativePath, string content)
        {
            var full = ctx.PathIn(relativePath);
            if (File.Exists(full) && !ctx.Force)
                return p.With(new FileAction.Skip(full, "already exists (use --force to overwrite)"));
            return p.With(new FileAction.Create(full, content));
        }

        // ---- solution + projects --------------------------------------------------------
        plan = Add(plan, $"{s}.sln", ctx.RenderRaw("Solution/Solution.sln.txt", new Dictionary<string, string>
        {
            ["Solution"] = s,
            ["DomainGuid"] = SolutionSpec.ProjectGuid($"{s}.Domain"),
            ["ApplicationGuid"] = SolutionSpec.ProjectGuid($"{s}.ApplicationService"),
            ["InfrastructureGuid"] = SolutionSpec.ProjectGuid($"{s}.Infrastructure"),
            ["ApiGuid"] = SolutionSpec.ProjectGuid($"{s}.API")
        }));

        // Tier 2 is OPT-IN, for the same reason the tool manifest is: a PackageReference to a
        // version that is not restorable from a configured feed makes the generated solution fail
        // to restore, and make:solution must never emit a solution that cannot build. doctor
        // reports Tier 2 as unavailable and prints the exact `dotnet add package` command.
        // Runtime and CLI ship in lockstep, so the CLI's version is the right default; they only
        // ever have to agree on the JSON envelope schema, not on assembly versions.
        // BOTH packages, not just the core one. This template IS Wolverine-based, so there is no
        // scenario where someone opts into the runtime here and does not want invoke:*. Shipping
        // only the core package left three manual steps between --with-runtime and a working
        // invoke:list — observed on a real project, and the reason this emits the pair.
        var runtimeReference = spec.WithRuntime
            ? $"    <!-- Tier 2: enables db:seed and invoke:*. -->{Environment.NewLine}" +
              $"    <PackageReference Include=\"Pitechy.Forge.Runtime\" Version=\"{ForgeVersion.Current}\" />{Environment.NewLine}" +
              $"    <PackageReference Include=\"Pitechy.Forge.Runtime.Wolverine\" Version=\"{ForgeVersion.Current}\" />"
            : string.Empty;

        var projectModel = new Dictionary<string, string>
        {
            ["Solution"] = s,
            ["TargetFramework"] = spec.TargetFramework,
            ["ForgeRuntimeReference"] = runtimeReference
        };

        plan = Add(plan, Path.Combine(domainDir, $"{s}.Domain.csproj"), ctx.RenderRaw("Solution/Domain.csproj.txt", projectModel));
        plan = Add(plan, Path.Combine(appDir, $"{s}.ApplicationService.csproj"), ctx.RenderRaw("Solution/Application.csproj.txt", projectModel));
        plan = Add(plan, Path.Combine(infraDir, $"{s}.Infrastructure.csproj"), ctx.RenderRaw("Solution/Infrastructure.csproj.txt", projectModel));
        plan = Add(plan, Path.Combine(apiDir, $"{s}.API.csproj"), ctx.RenderRaw("Solution/Api.csproj.txt", projectModel));

        // Pinning forge into a tool manifest is good practice — every teammate then gets the
        // version that generated the code — but it is OPT-IN, because a manifest takes
        // precedence over a global install: from the moment it exists, `dotnet forge` resolves
        // the LOCAL tool. If that exact version is not restorable from a configured feed, the
        // very next forge command fails with "Run dotnet tool restore". Writing it by default
        // therefore bricks forge in the solution it just created.
        if (spec.PinForge)
        {
            plan = Add(plan, Path.Combine(".config", "dotnet-tools.json"),
                ctx.RenderRaw("Solution/ToolManifest.json.txt", new Dictionary<string, string>
                {
                    ["ForgeVersion"] = ForgeVersion.Current
                }));
        }

        // ---- Domain ---------------------------------------------------------------------
        plan = Add(plan, Path.Combine(domainDir, "Enums", $"{spec.RoleEnum}.cs"),
            ctx.Render("Solution/UserRole.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = enumsNs,
                ["RoleEnum"] = spec.RoleEnum
            }));

        var authStub = spec.RoleGuardStyle == "single-array"
            ? "Solution/Authorization.cs.txt"
            : "Solution/AuthorizationSubRole.cs.txt";

        var authFile = spec.RoleGuardStyle == "single-array"
            ? "IRequireExplicitRoles.cs"
            : "IRequiresExplicitRoles.cs";

        plan = Add(plan, Path.Combine(domainDir, "Common", "Authorization", authFile),
            ctx.Render(authStub, new Dictionary<string, string>
            {
                ["Namespace"] = domainAuthNs,
                ["RoleEnum"] = spec.RoleEnum,
                ["RoleEnumNamespace"] = enumsNs
            }));

        // ---- ApplicationService ---------------------------------------------------------
        plan = Add(plan, Path.Combine(appDir, "Common", "Interfaces", "Authentication", "ICurrentUser.cs"),
            ctx.Render("Solution/ICurrentUser.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = appAuthNs,
                ["RoleEnum"] = spec.RoleEnum,
                ["RoleEnumNamespace"] = enumsNs
            }));

        plan = Add(plan, Path.Combine(appDir, "Common", "Interfaces", "Persistence", "Common", "IUnitOfWork.cs"),
            ctx.Render("Solution/IUnitOfWork.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = appPersistenceNs
            }));

        plan = Add(plan, Path.Combine(appDir, "Common", "Errors", "Errors.Authentication.cs"),
            ctx.Render("Solution/ErrorsAuthentication.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = appErrorsNs
            }));

        plan = Add(plan, Path.Combine(appDir, "Common", "Response", "PagedResult.cs"),
            ctx.Render("Solution/PagedResult.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = appResponseNs
            }));

        // ---- Infrastructure -------------------------------------------------------------
        plan = Add(plan, Path.Combine(infraDir, "Persistence", $"{spec.ResolvedDbContextName}.cs"),
            ctx.Render("Solution/DbContext.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = dbContextNs,
                ["DbContextName"] = spec.ResolvedDbContextName
            }));

        plan = Add(plan, Path.Combine(infraDir, "Persistence", "Repository", "Common", "UnitOfWork.cs"),
            ctx.Render("Solution/UnitOfWork.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = infraPersistenceNs,
                ["UnitOfWorkInterfaceNamespace"] = appPersistenceNs,
                ["DbContextName"] = spec.ResolvedDbContextName,
                ["DbContextNamespace"] = dbContextNs
            }));

        plan = Add(plan, Path.Combine(infraDir, "DependencyInjection.cs"),
            ctx.Render("Solution/DependencyInjection.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = infraNs,
                ["UnitOfWorkInterfaceNamespace"] = appPersistenceNs,
                ["InfraPersistenceNamespace"] = infraPersistenceNs
            }));

        // ---- API ------------------------------------------------------------------------
        // The middleware CONSUMES the marker interfaces, so it must vary with roleGuardStyle too.
        // Emitting the single-array middleware alongside role-and-subrole markers produces a
        // solution that does not compile.
        var middlewareStub = spec.RoleGuardStyle == "single-array"
            ? "Solution/RoleCheckMiddleware.cs.txt"
            : "Solution/RoleCheckMiddlewareSubRole.cs.txt";

        plan = Add(plan, Path.Combine(apiDir, "Middleware", "RoleCheckMiddleware.cs"),
            ctx.Render(middlewareStub, new Dictionary<string, string>
            {
                ["Namespace"] = apiMiddlewareNs,
                ["ApplicationAuthNamespace"] = appAuthNs,
                ["DomainAuthNamespace"] = domainAuthNs
            }));

        plan = Add(plan, Path.Combine(apiDir, "Common", "CurrentUser.cs"),
            ctx.Render("Solution/CurrentUser.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = appAuthNs,
                ["ApplicationAuthNamespace"] = appAuthNs,
                ["RoleEnum"] = spec.RoleEnum,
                ["RoleEnumNamespace"] = enumsNs
            }));

        plan = Add(plan, Path.Combine(apiDir, "Controllers", "ApiController.cs"),
            ctx.Render("Solution/ApiController.cs.txt", new Dictionary<string, string>
            {
                ["Namespace"] = $"{apiNs}.Controllers"
            }));

        plan = Add(plan, Path.Combine(apiDir, "Program.cs"),
            ctx.Render("Solution/Program.cs.txt", new Dictionary<string, string>
            {
                ["Solution"] = s,
                ["ApiMiddlewareNamespace"] = apiMiddlewareNs,
                ["ApplicationAuthNamespace"] = appAuthNs,
                ["ApplicationPersistenceNamespace"] = appPersistenceNs,
                ["InfrastructureNamespace"] = infraNs,
                ["DbContextNamespace"] = dbContextNs,
                ["DbContextName"] = spec.ResolvedDbContextName,
                ["CurrentUserRegistration"] = RenderCurrentUserRegistration(spec),
                ["ForgeRuntimeUsing"] = spec.WithRuntime
                    ? "using Forge.Runtime;" + Environment.NewLine +
                      "using Forge.Runtime.Wolverine;" + Environment.NewLine
                    : string.Empty,
                ["ForgeWolverineRegistration"] = spec.WithRuntime
                    ? Environment.NewLine +
                      "// Registers the invoke:list and invoke:run verb handlers. Adds no behaviour" + Environment.NewLine +
                      "// to a normal start — RunForgeRuntimeAsync only dispatches when forge launched" + Environment.NewLine +
                      "// this process." + Environment.NewLine +
                      "builder.Services.AddForgeWolverine();"
                    : string.Empty,
                ["ForgeRuntimeHook"] = spec.WithRuntime
                    ? Environment.NewLine +
                      "// forge hook. No-op on a normal start; short-circuits only when forge launched" + Environment.NewLine +
                      "// this process with a --forge:<verb> argument, so seeders run against the real" + Environment.NewLine +
                      "// container without the web server ever binding a port." + Environment.NewLine +
                      "if (await app.RunForgeRuntimeAsync(args)) return;" + Environment.NewLine
                    : string.Empty
            }));

        // ---- forge.config.json ----------------------------------------------------------
        var config = new ForgeConfig
        {
            Schema = "https://raw.githubusercontent.com/Pitechy/forge/main/schema/forge.config.v1.json",
            Version = ForgeConfig.CurrentVersion,
            Template = "onion-wolverine-erroror",
            SolutionName = s,
            DomainProject = ToConfigPath(domainDir),
            DomainNamespace = domainNs,
            ApplicationProject = ToConfigPath(appDir),
            ApplicationNamespace = appNs,
            ApplicationRepoPath = "Common/Interfaces/Persistence",
            ApplicationUnitOfWorkInterfacePath = "Common/Interfaces/Persistence/Common/IUnitOfWork.cs",
            ApplicationErrorsPath = "Common/Errors",
            InfrastructureProject = ToConfigPath(infraDir),
            InfrastructureNamespace = infraNs,
            InfrastructureRepoPath = "Persistence/Repository",
            InfrastructureUnitOfWorkImplPath = "Persistence/Repository/Common/UnitOfWork.cs",
            ApiProject = ToConfigPath(apiDir),
            ApiNamespace = apiNs,
            DbContextName = spec.ResolvedDbContextName,
            RoleEnum = spec.RoleEnum,
            RoleGuardStyle = spec.RoleGuardStyle,
            Scheduler = spec.Scheduler
        };

        plan = Add(plan, ConfigLoader.FileName,
            JsonSerializer.Serialize(config, ConfigLoader.JsonOptions) + Environment.NewLine);

        return PlanResult.Success(plan);
    }

    private static string ToConfigPath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Current-user registrations. Scoped normally — one identity per HTTP request, which is the
    /// only correct lifetime for a web app.
    ///
    /// Under a forge invocation they become SINGLETONS, because that process handles exactly one
    /// message and then exits. This is what makes `invoke:run --as-role Admin` work: Wolverine
    /// creates a scope per message, so an identity applied to a scoped instance would never reach
    /// the handler. Verified both ways — the same command is denied as Guest when scoped, and
    /// succeeds when singleton.
    ///
    /// The branch is deliberately visible here rather than hidden inside CurrentUser: it changes
    /// a security-relevant lifetime, so it belongs where a reviewer will see it. It cannot engage
    /// in production because forge refuses to run there at all.
    /// </summary>
    private static string RenderCurrentUserRegistration(SolutionSpec spec)
    {
        const string scoped = """
            builder.Services.AddScoped<CurrentUser>();
            builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
            builder.Services.AddScoped<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());
            """;

        if (!spec.WithRuntime) return scoped;

        return """
            if (ForgeRuntimeExtensions.IsForgeInvocation(args))
            {
                // forge invocation: one message, one process, so one identity for its lifetime.
                // Wolverine scopes each message, and a scoped identity would not reach the handler.
                builder.Services.AddSingleton<CurrentUser>();
                builder.Services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
                builder.Services.AddSingleton<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());
            }
            else
            {
                builder.Services.AddScoped<CurrentUser>();
                builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
                builder.Services.AddScoped<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());
            }
            """;
    }
}
