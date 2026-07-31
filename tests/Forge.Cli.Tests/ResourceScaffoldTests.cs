using Forge.Cli.Cli;
using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;
using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests;

public class ResourceScaffoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-res-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourceScaffoldTests()
    {
        var entities = Path.Combine(_root, "src", "App.Domain", "Entities");
        Directory.CreateDirectory(entities);
        File.WriteAllText(Path.Combine(entities, "Order.cs"), """
            namespace App.Domain.Entities;

            public class Order
            {
                public Guid Id { get; set; }
                public string Reference { get; set; } = string.Empty;
                public decimal Total { get; set; }
                public string CustomerEmail { get; set; } = string.Empty;
                private string Secret { get; set; } = string.Empty;
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private TemplateContext Context()
    {
        var config = new ForgeConfig
        {
            SolutionName = "App",
            DomainProject = "src/App.Domain",
            DomainNamespace = "App.Domain",
            ApplicationProject = "src/App.ApplicationService",
            ApplicationNamespace = "App.ApplicationService",
            InfrastructureProject = "src/App.Infrastructure",
            InfrastructureNamespace = "App.Infrastructure"
        };

        return new TemplateContext(config, _root, CodeStyle.Default,
            new StubRepository(_root, ".forge/stubs", "OnionWolverineErrorOr"), force: false);
    }

    private async Task<PlanResult> Plan(ResourceSpec spec) =>
        await new OnionWolverineErrorOrTemplate().PlanResource(Context(), spec, CancellationToken.None);

    private static ResourceSpec Spec(
        string? audience = null, string? view = null,
        string[]? only = null, string[]? exclude = null) =>
        new("Order", audience, view, only ?? [], exclude ?? []);

    private static string ContentOf(GenerationPlan plan, string endsWith) =>
        plan.Actions.OfType<FileAction.Create>().Single(a => a.Path.EndsWith(endsWith, StringComparison.Ordinal)).Content;

    [Theory]
    [InlineData(null, null, "OrderResponse.cs")]
    [InlineData("public", null, "OrderPublicResponse.cs")]
    [InlineData(null, "summary", "OrderSummaryResponse.cs")]
    [InlineData("admin", "summary", "OrderAdminSummaryResponse.cs")]
    public async Task Composes_names_from_audience_and_view(string? audience, string? view, string expected)
    {
        var plan = (await Plan(Spec(audience, view))).Plan;
        Assert.Contains(expected, plan.Actions.Select(a => Path.GetFileName(a.Path)));
    }

    [Fact]
    public async Task Derives_parameters_from_the_entity_and_ignores_non_public_members()
    {
        var content = ContentOf((await Plan(Spec())).Plan, "OrderResponse.cs");

        Assert.Contains("Guid Id", content);
        Assert.Contains("string Reference", content);
        Assert.Contains("decimal Total", content);
        Assert.DoesNotContain("Secret", content);
    }

    /// <summary>
    /// The security point of the whole taxonomy: an audience shape must be able to drop fields
    /// that must not reach that caller.
    /// </summary>
    [Fact]
    public async Task Only_and_exclude_shape_the_response()
    {
        var only = ContentOf((await Plan(Spec("public", only: ["Id", "Reference"]))).Plan, "OrderPublicResponse.cs");
        Assert.Contains("Guid Id", only);
        Assert.Contains("string Reference", only);
        Assert.DoesNotContain("CustomerEmail", only);

        var excluded = ContentOf((await Plan(Spec("admin", exclude: ["CustomerEmail"]))).Plan, "OrderAdminResponse.cs");
        Assert.DoesNotContain("CustomerEmail", excluded);
        Assert.Contains("decimal Total", excluded);
    }

    [Fact]
    public async Task First_resource_creates_the_mapping_config_with_a_registration()
    {
        var content = ContentOf((await Plan(Spec())).Plan, "OrderMappingConfig.cs");
        Assert.Contains("config.NewConfig<Order, OrderResponse>();", content);
    }

    /// <summary>
    /// The resources folder is pluralised: a folder named "Order" makes the namespace end in
    /// ".Order", after which the identifier Order resolves to the namespace, not the entity
    /// (CS0118). Regression test for a bug found by compiling the output.
    /// </summary>
    [Fact]
    public async Task Resources_live_in_a_pluralised_folder_to_avoid_namespace_collision()
    {
        var path = (await Plan(Spec())).Plan.Actions.First().Path.Replace('\\', '/');

        Assert.Contains("/Resources/Orders/", path);
        Assert.DoesNotContain("/Resources/Order/", path);
    }

    [Fact]
    public async Task Rejects_an_unconfigured_audience()
    {
        var result = await Plan(Spec("superuser"));

        Assert.False(result.Ok);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Public, Customer, Admin, Partner", result.Error);
    }

    [Fact]
    public async Task Rejects_unknown_properties_and_lists_the_real_ones()
    {
        var result = await Plan(Spec(only: ["Nope"]));

        Assert.False(result.Ok);
        Assert.Contains("Nope", result.Error);
        Assert.Contains("Reference", result.Error);
    }

    [Fact]
    public async Task Rejects_only_combined_with_exclude()
    {
        var result = await Plan(Spec(only: ["Id"], exclude: ["Total"]));

        Assert.False(result.Ok);
        Assert.Contains("mutually exclusive", result.Error);
    }

    [Fact]
    public async Task Requires_the_entity_to_exist()
    {
        var result = await new OnionWolverineErrorOrTemplate()
            .PlanResource(Context(), new ResourceSpec("Ghost", null, null, [], []), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("make:entity", result.Error);
    }

    [Fact]
    public async Task Planning_writes_nothing_to_disk()
    {
        var appDir = Path.Combine(_root, "src", "App.ApplicationService");
        _ = await Plan(Spec());
        Assert.False(Directory.Exists(appDir), "Planning must never touch the filesystem.");
    }
}
