using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;
using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests;

/// <summary>
/// make:enum, and the using resolution that makes an enum usable from an entity.
/// </summary>
public class EnumScaffoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-enum-" + Guid.NewGuid().ToString("N")[..8]);

    public EnumScaffoldTests()
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

    private TemplateContext Context()
    {
        var loaded = ConfigLoader.Load(_root);
        Assert.True(loaded.Ok, loaded.Error);

        return new TemplateContext(
            loaded.Config!, loaded.SolutionRoot!, CodeStyle.Default,
            new StubRepository(loaded.SolutionRoot!, loaded.Config!.StubOverridesPath, "OnionWolverineErrorOr"),
            force: false);
    }

    private string Enum(string name, string values, bool flags = false)
    {
        var parsed = EnumValueParser.Parse(values, flags);
        Assert.True(parsed.Ok, parsed.Error);

        var result = EnumScaffold.Plan(Context(), new EnumSpec(name, parsed.Members, flags));
        Assert.True(result.Ok, result.Error);

        return result.Plan.Actions.OfType<FileAction.Create>().Single().Content;
    }

    // ---- parsing -----------------------------------------------------------------------------

    [Fact]
    public void Unnumbered_members_are_emitted_as_written()
    {
        var text = Enum("OrderStatus", "Pending,Paid,Shipped");

        Assert.Contains("public enum OrderStatus", text);
        Assert.Contains("    Pending,\n    Paid,\n    Shipped", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("[Flags]", text);
    }

    [Fact]
    public void Explicit_values_are_preserved()
    {
        var text = Enum("OrderStatus", "Pending = 1, Paid=5,Shipped = 9");

        Assert.Contains("Pending = 1", text);
        Assert.Contains("Paid = 5", text);
        Assert.Contains("Shipped = 9", text);
    }

    /// <summary>
    /// A [Flags] enum numbered 0,1,2,3 is the classic silent bug: 3 is not a distinct flag, it
    /// is Read|Write, and combinations start overlapping.
    /// </summary>
    [Fact]
    public void Flags_members_are_numbered_as_powers_of_two()
    {
        var text = Enum("Permission", "None,Read,Write,Delete", flags: true);

        Assert.Contains("[Flags]", text);
        Assert.Contains("None = 0", text);
        Assert.Contains("Read = 1", text);
        Assert.Contains("Write = 2", text);
        Assert.Contains("Delete = 4", text);
    }

    /// <summary>
    /// Only a member actually named None gets 0. Keying off position would give this enum a
    /// Read of 0 — "no flags set" — so it would never match anything.
    /// </summary>
    [Fact]
    public void Flags_without_a_None_member_still_start_at_one()
    {
        var text = Enum("Permission", "Read,Write,Delete", flags: true);

        Assert.Contains("Read = 1", text);
        Assert.Contains("Write = 2", text);
        Assert.Contains("Delete = 4", text);
        Assert.DoesNotContain("= 0", text);
    }

    [Fact]
    public void Explicit_flag_values_are_not_renumbered()
    {
        var text = Enum("Permission", "None=0,Read=8,Write=16", flags: true);

        Assert.Contains("Read = 8", text);
        Assert.Contains("Write = 16", text);
    }

    [Theory]
    [InlineData("Pending,Pending", "twice")]
    [InlineData("Pending,2Bad", "valid C# identifier")]
    [InlineData("Pending=x", "whole number")]
    [InlineData("", "No values")]
    public void Malformed_values_are_a_clean_error(string values, string expected)
    {
        var parsed = EnumValueParser.Parse(values);

        Assert.False(parsed.Ok);
        Assert.Contains(expected, parsed.Error);
    }

    // ---- the integration that makes enums usable ---------------------------------------------

    /// <summary>
    /// The reason make:enum needed a fix elsewhere to be worth anything: the enum lands in the
    /// domain's Enums namespace and the entity in Entities, so without a using the entity does
    /// not compile. Verified end to end against a real build.
    /// </summary>
    [Fact]
    public void An_entity_using_an_enum_gets_the_namespace_imported()
    {
        File.WriteAllText(Path.Combine(_root, "src", "App.Domain", "OrderStatus.cs"),
            "namespace App.Domain.Enums;\n\npublic enum OrderStatus { Pending, Paid }");

        var result = EntityScaffold.Plan(Context(), new EntitySpec("Order",
            PropertyParser.Parse("Status:OrderStatus,Total:decimal").Properties, SkipDbSet: true));

        Assert.True(result.Ok, result.Error);

        var entity = result.Plan.Actions.OfType<FileAction.Create>()
            .Single(a => a.Path.EndsWith("Order.cs", StringComparison.Ordinal)).Content;

        Assert.Contains("using App.Domain.Enums;", entity);
    }

    /// <summary>A type in the entity's own namespace needs no using.</summary>
    [Fact]
    public void A_type_in_the_same_namespace_is_not_imported()
    {
        File.WriteAllText(Path.Combine(_root, "src", "App.Domain", "Money.cs"),
            "namespace App.Domain.Entities;\n\npublic class Money { }");

        var result = EntityScaffold.Plan(Context(), new EntitySpec("Order",
            PropertyParser.Parse("Price:Money").Properties, SkipDbSet: true));

        Assert.True(result.Ok, result.Error);

        var entity = result.Plan.Actions.OfType<FileAction.Create>()
            .Single(a => a.Path.EndsWith("Order.cs", StringComparison.Ordinal)).Content;

        Assert.DoesNotContain("using App.Domain.Entities;", entity);
    }
}
