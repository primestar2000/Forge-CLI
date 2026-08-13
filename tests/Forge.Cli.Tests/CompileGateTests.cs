using Forge.Cli.Cli;
using Forge.Cli.Templates;
using Forge.Cli.Tests.Infrastructure;

namespace Forge.Cli.Tests;

/// <summary>
/// The slow layer: scaffold into a temp directory with the real generators, then compile with the
/// real SDK.
///
/// Run separately from the fast suite:
///   dotnet test --filter Category!=Compile     (fast: plans + Roslyn)
///   dotnet test --filter Category=Compile      (slow: these)
///
/// Nearly every genuine defect found while building forge was caught here rather than by
/// plan-level assertions — NuGet version conflicts, missing using directives, a missing
/// package reference, an ambiguous method-group conversion, and a namespace/type collision.
/// None of those are visible to a test that only inspects a GenerationPlan.
/// </summary>
[Trait("Category", "Compile")]
public class CompileGateTests
{
    /// <summary>Both role-guard styles must produce a solution that builds, warnings included.</summary>
    [Theory]
    [InlineData("single-array")]
    [InlineData("role-and-subrole")]
    public async Task Bare_scaffolded_solution_compiles_clean(string roleGuardStyle)
    {
        using var harness = new ScaffoldHarness();
        await harness.MakeSolution("Gate", roleGuardStyle);

        AssertClean(harness.Build());
    }

    /// <summary>
    /// The full stack, generated end to end with no hand-written files. This is the scenario a
    /// developer actually runs on day one.
    /// </summary>
    [Fact]
    public async Task Full_stack_compiles_clean()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");
        await harness.MakeEntity("Order", "Reference:string,Total:decimal,CustomerEmail:string,PlacedOn:DateTime");
        await harness.MakeEntity("Product", "Name:string,Price:decimal,Notes:string?");

        await harness.MakeResource("Order");
        await harness.MakeResource("Order", audience: "public", only: ["Id", "Reference"]);
        await harness.MakeResource("Order", audience: "admin", view: "summary", exclude: ["CustomerEmail"]);

        await harness.MakeRepository("Order");
        await harness.MakeRepository("Product");

        await harness.MakeFeature("PlaceOrder", MessageKind.Command, ["User"], group: "Orders",
            properties: "Reference:string,Total:decimal");
        await harness.MakeFeature("GetOrder", MessageKind.Query, ["Admin"], group: "Orders",
            returns: "OrderAdminSummaryResponse");
        await harness.MakeFeature("Ping", MessageKind.Query, [], anonymous: true);

        AssertClean(harness.Build());
    }

    /// <summary>
    /// Both entity shapes must build warning-free, and warnings are the point here: the
    /// encapsulated shape materialises through a parameterless constructor, so a non-nullable
    /// property without an initializer warns CS8618 on every entity. Only the compiler sees it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_entity_shapes_compile_clean(bool encapsulated)
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");

        // Event collides with a keyword once camelCased into a constructor parameter.
        await harness.MakeEntity("Order",
            "Reference:string,Total:decimal,Notes:string?,Event:string,PlacedOn:DateTime", encapsulated);

        await harness.MakeRepository("Order");
        await harness.MakeResource("Order");

        AssertClean(harness.Build());
    }

    /// <summary>
    /// An enum used as an entity property.
    ///
    /// The enum lands in the domain's Enums namespace and the entity in Entities, so the entity
    /// needs a using that nothing else supplies. It did not get one — UsingsFor suppressed every
    /// namespace when ImplicitUsings was on, project-local ones included — and the generated
    /// entity did not compile. A plan assertion cannot see that; this does.
    /// </summary>
    [Fact]
    public async Task An_entity_with_an_enum_property_compiles_clean()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");

        await harness.MakeEnum("OrderStatus", "Pending,Paid=5,Shipped");
        await harness.MakeEnum("Permission", "None,Read,Write,Delete", flags: true);

        await harness.MakeEntity("Order", "Reference:string,Status:OrderStatus,Access:Permission");
        await harness.MakeRepository("Order");

        AssertClean(harness.Build());
    }

    /// <summary>
    /// The enum has to reach every generated file that names it, not just the entity.
    ///
    /// From a field report: "Book and BookResponse were generated without
    /// using Library.Domain.Enums — solution didn't compile until I hand-fixed 2 files." The
    /// entity was fixed first and the response and message record were missed, because each
    /// generator hardcoded its own using list. This covers all three at once.
    /// </summary>
    [Fact]
    public async Task An_enum_reaches_the_entity_the_response_and_the_message()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");

        await harness.MakeEnum("BookStatus", "Available,Borrowed,Lost");
        await harness.MakeEntity("Book", "Title:string,Status:BookStatus");

        // The response projects the entity, so it inherits the enum-typed property.
        await harness.MakeResource("Book");
        await harness.MakeResource("Book", audience: "admin");

        // The message record names the enum directly via --properties.
        await harness.MakeFeature("BorrowBook", MessageKind.Command, ["User"], group: "Books",
            properties: "Title:string,Status:BookStatus");

        await harness.MakeRepository("Book");

        AssertClean(harness.Build());
    }

    /// <summary>
    /// A base class carrying Id changes where EF finds the key and where every repository's
    /// generic constraint resolves. Redeclaring an inherited member is a CS0108 warning, which
    /// this gate fails on — and none of it is visible to a plan assertion.
    /// </summary>
    [Fact]
    public async Task A_solution_with_a_base_entity_compiles_clean()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array", withBaseEntity: true);

        await harness.MakeEntity("Order", "Reference:string,Total:decimal");

        // CreatedAt is already on BaseEntity: asking for it again must not redeclare it.
        await harness.MakeEntity("Product", "Name:string,CreatedAt:DateTime");

        await harness.MakeRepository("Order");
        await harness.MakeResource("Order");

        AssertClean(harness.Build());
    }

    /// <summary>
    /// Re-running every generator must be a no-op that still compiles. A patcher that inserted a
    /// duplicate member would break the build here even though each individual patch reported
    /// success.
    /// </summary>
    [Fact]
    public async Task Regenerating_everything_is_idempotent_and_still_compiles()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");
        await harness.MakeResource("Order");
        await harness.MakeRepository("Order");
        await harness.MakeFeature("PlaceOrder", MessageKind.Command, ["User"], group: "Orders");

        AssertClean(harness.Build());

        // Second pass — everything should skip.
        var entity = await harness.MakeEntity("Order", "Reference:string,Total:decimal");
        var resource = await harness.MakeResource("Order");
        var repository = await harness.MakeRepository("Order");
        var feature = await harness.MakeFeature("PlaceOrder", MessageKind.Command, ["User"], group: "Orders");

        Assert.Equal(0, entity.Created + entity.Patched);
        Assert.Equal(0, resource.Created + resource.Patched);
        Assert.Equal(0, repository.Created + repository.Patched);
        Assert.Equal(0, feature.Created + feature.Patched);

        AssertClean(harness.Build());
    }

    /// <summary>
    /// Two entities patch the same DbContext, IUnitOfWork and UnitOfWork. Compiling proves the
    /// second patch did not disturb the first — something the token-subsequence invariant checks
    /// structurally but only the compiler confirms semantically.
    /// </summary>
    [Fact]
    public async Task Repeated_patching_of_shared_files_compiles()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");

        foreach (var name in new[] { "Order", "Product", "Customer", "Invoice" })
        {
            await harness.MakeEntity(name, "Name:string,Amount:decimal");
            await harness.MakeRepository(name);
            await harness.MakeResource(name, audience: "admin");
        }

        AssertClean(harness.Build());
    }

    /// <summary>
    /// Compilation is not enough. make:repo adds a repository to the UnitOfWork constructor; if
    /// nothing registers it, the solution builds perfectly and the host then fails to start with
    /// "Unable to resolve service". Observed for real — this asserts the container is satisfiable.
    /// </summary>
    [Fact]
    public async Task Generated_host_can_actually_start()
    {
        if (!EfTool.IsInstalled())
        {
            Assert.Fail("dotnet-ef is required for this gate: dotnet tool install --global dotnet-ef");
        }

        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");
        await harness.MakeEntity("Product", "Name:string,Price:decimal");
        await harness.MakeRepository("Order");
        await harness.MakeRepository("Product");

        AssertClean(harness.Build());

        var host = harness.VerifyHostStarts();
        Assert.True(host.ExitCode == 0,
            "Generated host failed to start (DI registration gap?):\n" + host.Output);
    }

    private static void AssertClean(BuildOutcome build)
    {
        Assert.True(build.ExitCode == 0,
            "Generated solution failed to build:\n" + string.Join("\n", build.Errors));

        // Warnings matter as much as errors here: they are how a code-style mismatch shows up
        // (CS8618 on a non-nullable property, an unused using, an async method with no await).
        Assert.True(build.Warnings.Count == 0,
            "Generated solution built with warnings:\n" + string.Join("\n", build.Warnings));
    }
}
