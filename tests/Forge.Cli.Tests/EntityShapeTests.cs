using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;
using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests;

/// <summary>
/// The shape make:entity emits.
///
/// Encapsulated is the DEFAULT: this is an onion/DDD template, and an entity anyone can mutate
/// field by field contradicts the architecture the template's own name promises.
/// --public-setters opts out for one entity; encapsulateEntities:false opts out for a solution.
/// </summary>
public class EntityShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-ent-" + Guid.NewGuid().ToString("N")[..8]);

    public EntityShapeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "App.Domain"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "App.Infrastructure"));

        File.WriteAllText(Path.Combine(_root, "forge.config.json"), """
            {
              "version": 1,
              "template": "onion-wolverine-erroror",
              "solutionName": "App",
              "domainProject": "src/App.Domain",
              "domainNamespace": "App.Domain",
              "infrastructureProject": "src/App.Infrastructure",
              "infrastructureNamespace": "App.Infrastructure"
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Entity(string properties, bool encapsulated = true)
    {
        var loaded = ConfigLoader.Load(_root);
        Assert.True(loaded.Ok, loaded.Error);

        var ctx = new TemplateContext(
            loaded.Config!, loaded.SolutionRoot!, CodeStyle.Default,
            new StubRepository(loaded.SolutionRoot!, loaded.Config!.StubOverridesPath, "OnionWolverineErrorOr"),
            force: false);

        var spec = new EntitySpec("Order", PropertyParser.Parse(properties).Properties,
            SkipDbSet: true, Encapsulated: encapsulated);

        var result = EntityScaffold.Plan(ctx, spec);
        Assert.True(result.Ok, result.Error);

        return result.Plan.Actions
            .OfType<FileAction.Create>()
            .Single(a => a.Path.EndsWith("Order.cs", StringComparison.Ordinal))
            .Content;
    }

    [Fact]
    public void Encapsulated_is_the_default_shape()
    {
        var entity = Entity("Reference:string,Total:decimal");

        Assert.Contains("public Guid Id { get; private set; }", entity);
        Assert.Contains("public string Reference { get; private set; } = string.Empty;", entity);
        Assert.Contains("public decimal Total { get; private set; }", entity);
        Assert.DoesNotContain("{ get; set; }", entity);
    }

    /// <summary>EF Core materialises through a parameterless constructor.</summary>
    [Fact]
    public void It_emits_a_private_constructor_for_EF_and_a_public_one_for_callers()
    {
        var entity = Entity("Reference:string,Total:decimal");

        Assert.Contains("private Order()", entity);
        Assert.Contains("public Order(string reference, decimal total)", entity);
        Assert.Contains("Reference = reference;", entity);
        Assert.Contains("Total = total;", entity);
    }

    /// <summary>
    /// Private setters with no mutator produce an entity that can be created and never changed,
    /// which makes every update handler impossible to write.
    /// </summary>
    [Fact]
    public void It_emits_an_Update_method()
    {
        var entity = Entity("Reference:string,Total:decimal");

        Assert.Contains("public void Update(string reference, decimal total)", entity);
    }

    /// <summary>
    /// A property named Event, Class or Namespace camelCases onto a keyword. Without escaping,
    /// the generated constructor does not parse — and this only shows up on someone's real
    /// domain model, never on Name/Price test data.
    /// </summary>
    [Fact]
    public void Parameters_that_collide_with_keywords_are_escaped()
    {
        var entity = Entity("Event:string,Class:string,Total:decimal");

        Assert.Contains("public Order(string @event, string @class, decimal total)", entity);
        Assert.Contains("Event = @event;", entity);
        Assert.Contains("Class = @class;", entity);
    }

    /// <summary>
    /// With no properties the generated public constructor would be parameterless and collide
    /// with the private EF one — the file would not compile.
    /// </summary>
    [Fact]
    public void An_entity_with_no_properties_emits_only_the_private_constructor()
    {
        var entity = Entity("");

        Assert.Contains("private Order()", entity);
        Assert.DoesNotContain("public Order(", entity);
        Assert.DoesNotContain("public void Update(", entity);
    }

    /// <summary>
    /// Non-nullable strings still need an initializer: the EF constructor is parameterless, so
    /// without one every encapsulated entity warns CS8618 — and the gate fails on warnings.
    /// </summary>
    [Fact]
    public void Non_nullable_strings_keep_their_initializer()
    {
        var entity = Entity("Reference:string,Notes:string?");

        Assert.Contains("public string Reference { get; private set; } = string.Empty;", entity);
        Assert.Contains("public string? Notes { get; private set; }", entity);
        Assert.DoesNotContain("Notes { get; private set; } = string.Empty;", entity);
    }

    [Fact]
    public void Public_setters_opts_out_entirely()
    {
        var entity = Entity("Reference:string,Total:decimal", encapsulated: false);

        Assert.Contains("public Guid Id { get; set; }", entity);
        Assert.Contains("public string Reference { get; set; } = string.Empty;", entity);
        Assert.DoesNotContain("private set", entity);
        Assert.DoesNotContain("public void Update(", entity);
        Assert.DoesNotContain("private Order()", entity);
    }
}
