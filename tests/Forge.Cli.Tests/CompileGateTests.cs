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
    /// Relationships, all the way to a live EF model.
    ///
    /// Compiling is not enough here: a bad HasForeignKey or a navigation EF cannot bind produces
    /// code that builds and then throws when the model is first constructed. VerifyHostStarts
    /// builds the real model, so it catches that.
    /// </summary>
    [Fact]
    public async Task Entities_with_relationships_compile_and_build_a_valid_model()
    {
        if (!EfTool.IsInstalled())
        {
            Assert.Fail("dotnet-ef is required for this gate: dotnet tool install --global dotnet-ef");
        }

        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");

        await harness.MakeEntity("Author", "Name:string");
        await harness.MakeEntity("Publisher", "Name:string");

        // Required and optional in one entity: they take different delete behaviours, and an
        // optional navigation must not carry the = null! a required one needs.
        await harness.MakeEntity("Book", "Title:string", true, "Author", "Publisher?");

        await harness.MakeRepository("Book");
        await harness.MakeResource("Book");

        AssertClean(harness.Build());

        var host = harness.VerifyHostStarts();
        Assert.True(host.ExitCode == 0,
            "EF could not build a model from the generated relationships:\n" + host.Output);
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

    /// <summary>
    /// Swagger is on by default, so it is now part of what every scaffold must compile with —
    /// a bad package version or a misplaced statement would break `make:solution` itself rather
    /// than some opt-in path. The zero-warning assertion also catches a Swashbuckle version
    /// whose transitive dependencies downgrade the pinned EF Core 8 packages (NU1605).
    /// </summary>
    [Fact]
    public async Task A_solution_with_swagger_compiles_and_starts()
    {
        if (!EfTool.IsInstalled())
        {
            Assert.Fail("dotnet-ef is required for this gate: dotnet tool install --global dotnet-ef");
        }

        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array");
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");
        await harness.MakeRepository("Order");

        AssertClean(harness.Build());

        var program = await File.ReadAllTextAsync(
            Path.Combine(harness.SolutionDirectory, "src", "Gate.API", "Program.cs"));

        Assert.Contains("builder.Services.AddSwaggerGen();", program);
        Assert.Contains("app.UseSwaggerUI();", program);

        var host = harness.VerifyHostStarts();
        Assert.True(host.ExitCode == 0,
            "Generated host failed to start with Swagger wired:\n" + host.Output);
    }

    /// <summary>
    /// The opt-out has to produce a solution that is clean, not merely one that builds: the
    /// orphaned AddEndpointsApiExplorer() this replaced was dead code in every solution forge
    /// ever generated, and re-introducing it under --no-swagger would repeat that.
    /// </summary>
    [Fact]
    public async Task A_solution_without_swagger_has_no_swagger_wiring_at_all()
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array", swagger: false);
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");

        AssertClean(harness.Build());

        var apiDirectory = Path.Combine(harness.SolutionDirectory, "src", "Gate.API");
        var program = await File.ReadAllTextAsync(Path.Combine(apiDirectory, "Program.cs"));
        var csproj = await File.ReadAllTextAsync(Path.Combine(apiDirectory, "Gate.API.csproj"));

        Assert.DoesNotContain("Swagger", program);
        Assert.DoesNotContain("AddEndpointsApiExplorer", program);
        Assert.DoesNotContain("Swashbuckle", csproj);
    }

    /// <summary>
    /// The gate this project was missing, and the reason a real user's solution broke.
    ///
    /// Every other test here proves the scaffold builds. None of them could prove the dependency
    /// graph can still MOVE — and the previous pins could not. WolverineFx 3.6.1 caps every
    /// Microsoft.Extensions.* package below 10.0.0 on all of its framework groups; a net10.0
    /// target resolves them at 10.x. The scaffold restored, built, ran and passed every gate,
    /// then produced an unsatisfiable NU1107 the first time a developer added a package that
    /// pulled the current generation in.
    ///
    /// Microsoft.Extensions.Hosting rather than a database provider, and floated within the
    /// framework's own major rather than "latest": it is the package the cap actually applies
    /// to, and deriving the version from the TFM keeps the test from drifting as new majors
    /// ship or breaking on a machine whose default framework is not the newest.
    /// </summary>
    /// <remarks>
    /// Run per framework, with the framework named explicitly. Relying on the default meant the
    /// gate only ever saw net8.0 — the test host is net8.0 — so the net10.0 configuration that
    /// every user on a current SDK receives was never built here at all.
    /// </remarks>
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task The_generated_dependency_graph_can_still_move(string targetFramework)
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array", targetFramework: targetFramework);
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");

        AssertClean(harness.Build());

        var major = targetFramework.Replace("net", string.Empty).Split('.')[0];

        var added = harness.AddPackage(
            "Gate.API", "Microsoft.Extensions.Hosting", $"{major}.*");

        Assert.True(added.ExitCode == 0,
            $"The {targetFramework} scaffold cannot accept Microsoft.Extensions.Hosting " +
            $"{major}.x, so its dependency graph is frozen:\n" + added.Output);

        // NU1510 is about the PROBE, not the scaffold: on a framework that ships
        // Microsoft.Extensions.Hosting in its shared framework, referencing it explicitly is
        // redundant and the SDK says so. That is precisely why it makes a good probe — it is
        // the package Wolverine's version cap applies to — so the warning is expected here and
        // nowhere else. NU1608 and NU1605, the two that signal a frozen graph, still fail.
        AssertClean(harness.Build(), ignoreWarningCodes: "NU1510");
    }

    /// <summary>
    /// The plain build, per framework. Cheap next to the graph test above and it isolates the
    /// failure: a row that cannot even compile is a different problem from one that compiles and
    /// then cannot accept a package.
    /// </summary>
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task Every_supported_framework_scaffolds_and_builds_clean(string targetFramework)
    {
        using var harness = new ScaffoldHarness();

        await harness.MakeSolution("Gate", "single-array", targetFramework: targetFramework);
        await harness.MakeEntity("Order", "Reference:string,Total:decimal");
        await harness.MakeRepository("Order");

        AssertClean(harness.Build());
    }

    private static void AssertClean(BuildOutcome build, params string[] ignoreWarningCodes)
    {
        Assert.True(build.ExitCode == 0,
            "Generated solution failed to build:\n" + string.Join("\n", build.Errors));

        // Warnings matter as much as errors here: they are how a code-style mismatch shows up
        // (CS8618 on a non-nullable property, an unused using, an async method with no await)
        // and how an incoherent dependency graph shows up (NU1605 downgrade, NU1608 constraint).
        var warnings = build.Warnings
            .Where(w => !ignoreWarningCodes.Any(code => w.Contains(code, StringComparison.Ordinal)))
            .ToList();

        Assert.True(warnings.Count == 0,
            "Generated solution built with warnings:\n" + string.Join("\n", warnings));
    }
}
