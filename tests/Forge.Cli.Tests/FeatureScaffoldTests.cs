using Forge.Cli.Cli;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Tests;

public class FeatureScaffoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-feat-" + Guid.NewGuid().ToString("N")[..8]);

    public FeatureScaffoldTests()
    {
        // A minimal domain so role validation has a real UserRole enum to check against.
        var enums = Path.Combine(_root, "src", "App.Domain", "Enums");
        Directory.CreateDirectory(enums);
        File.WriteAllText(Path.Combine(enums, "UserRole.cs"),
            "namespace App.Domain.Enums;\npublic enum UserRole { Guest, User, Admin }\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private TemplateContext Context(string roleGuardStyle = "single-array")
    {
        var config = new ForgeConfig
        {
            SolutionName = "App",
            DomainProject = "src/App.Domain",
            DomainNamespace = "App.Domain",
            ApplicationProject = "src/App.ApplicationService",
            ApplicationNamespace = "App.ApplicationService",
            InfrastructureProject = "src/App.Infrastructure",
            InfrastructureNamespace = "App.Infrastructure",
            RoleGuardStyle = roleGuardStyle
        };

        return new TemplateContext(
            config, _root, CodeStyle.Default,
            new StubRepository(_root, ".forge/stubs", "OnionWolverineErrorOr"),
            force: false);
    }

    private static FeatureSpec Spec(
        string name = "CreateGig",
        MessageKind kind = MessageKind.Command,
        string[]? roles = null,
        bool anonymous = false,
        string? group = "Gigs",
        string? properties = null) =>
        new(name, kind,
            PropertyParser.Parse(properties).Properties,
            roles ?? ["User"],
            anonymous, group, null, false, null);

    private async Task<PlanResult> Plan(FeatureSpec spec, string roleGuardStyle = "single-array") =>
        await new Templates.OnionWolverineErrorOr.OnionWolverineErrorOrTemplate()
            .PlanFeature(Context(roleGuardStyle), spec, CancellationToken.None);

    private static string ContentOf(GenerationPlan plan, string endsWith) =>
        plan.Actions.OfType<FileAction.Create>().Single(a => a.Path.EndsWith(endsWith, StringComparison.Ordinal)).Content;

    [Fact]
    public async Task Emits_message_handler_and_validator()
    {
        var plan = (await Plan(Spec())).Plan;
        var files = plan.Actions.Select(a => Path.GetFileName(a.Path)).ToList();

        Assert.Contains("CreateGigCommand.cs", files);
        Assert.Contains("CreateGigCommandHandler.cs", files);
        Assert.Contains("CreateGigCommandValidator.cs", files);
    }

    /// <summary>
    /// The reason roleGuardStyle exists: one tool, two team conventions, no fork.
    /// </summary>
    [Fact]
    public async Task Role_guard_style_changes_the_emitted_shape()
    {
        var single = ContentOf((await Plan(Spec(roles: ["User", "Admin"]))).Plan, "CreateGigCommand.cs");
        Assert.Contains(": IRequireExplicitRoles", single);
        Assert.Contains("public UserRole[] Roles => [UserRole.User, UserRole.Admin];", single);
        Assert.DoesNotContain("AllowedSubRoles", single);

        var sub = ContentOf((await Plan(Spec(roles: ["Admin"]), "role-and-subrole")).Plan, "CreateGigCommand.cs");
        Assert.Contains(": IRequiresExplicitRoles", sub);
        Assert.Contains("public UserRole[] AllowedRoles => [UserRole.Admin];", sub);
        Assert.Contains("public string[] AllowedSubRoles => [];", sub);
    }

    [Fact]
    public async Task Anonymous_emits_the_bypass_marker_for_each_style()
    {
        Assert.Contains(": IAllowRoleCheckBypass",
            ContentOf((await Plan(Spec(anonymous: true, roles: []))).Plan, "CreateGigCommand.cs"));

        Assert.Contains(": IAllowAnonymousRoles",
            ContentOf((await Plan(Spec(anonymous: true, roles: []), "role-and-subrole")).Plan, "CreateGigCommand.cs"));
    }

    [Fact]
    public async Task Query_goes_under_Queries_and_gets_a_Query_suffix()
    {
        var plan = (await Plan(Spec("ListGigs", MessageKind.Query))).Plan;
        var path = plan.Actions.First().Path.Replace('\\', '/');

        Assert.Contains("/Queries/ListGigs/", path);
        Assert.Contains("ListGigsQuery.cs", plan.Actions.Select(a => Path.GetFileName(a.Path)));
    }

    [Fact]
    public async Task Properties_become_record_parameters_and_validation_rules()
    {
        var plan = (await Plan(Spec(properties: "Name:string,BudgetMin:int,Notes:string?"))).Plan;

        var message = ContentOf(plan, "CreateGigCommand.cs");
        Assert.Contains("string Name", message);
        Assert.Contains("int BudgetMin", message);
        Assert.Contains("string? Notes", message);

        // Required strings get a NotEmpty rule; nullable ones must not.
        var validator = ContentOf(plan, "CreateGigCommandValidator.cs");
        Assert.Contains("RuleFor(x => x.Name).NotEmpty();", validator);
        Assert.DoesNotContain("Notes", validator);
    }

    /// <summary>
    /// RoleCheckMiddleware denies any message implementing neither marker, so generating one
    /// would produce a handler that always throws at runtime. Refuse at generation time.
    /// </summary>
    [Fact]
    public async Task Refuses_a_message_with_no_guard_at_all()
    {
        var result = await Plan(Spec(roles: []));

        Assert.False(result.Ok);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--anonymous", result.Error);
    }

    [Fact]
    public async Task Rejects_roles_that_are_not_in_the_enum()
    {
        var result = await Plan(Spec(roles: ["Manager"]));

        Assert.False(result.Ok);
        Assert.Contains("Manager", result.Error);
        Assert.Contains("Guest, User, Admin", result.Error);
    }

    [Fact]
    public async Task Rejects_anonymous_combined_with_roles()
    {
        var result = await Plan(Spec(roles: ["User"], anonymous: true));

        Assert.False(result.Ok);
        Assert.Contains("mutually exclusive", result.Error);
    }

    [Fact]
    public async Task Ungrouped_features_skip_the_redundant_nesting()
    {
        var plan = (await Plan(Spec(group: null))).Plan;
        var path = plan.Actions.First().Path.Replace('\\', '/');

        Assert.Contains("/Features/CreateGig/", path);
        Assert.DoesNotContain("/Commands/", path);
    }

    [Fact]
    public async Task Planning_writes_nothing_to_disk()
    {
        var appDir = Path.Combine(_root, "src", "App.ApplicationService");
        _ = await Plan(Spec());
        Assert.False(Directory.Exists(appDir), "Planning must never touch the filesystem.");
    }
}
