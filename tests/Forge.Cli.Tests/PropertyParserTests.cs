using Forge.Cli.Templates;

namespace Forge.Cli.Tests;

public class PropertyParserTests
{
    [Fact]
    public void Parses_names_types_and_nullability()
    {
        var result = PropertyParser.Parse("Name:string, Price:decimal ,Notes:string?,Count:int");

        Assert.True(result.Ok, result.Error);
        Assert.Collection(result.Properties,
            p => { Assert.Equal("Name", p.Name); Assert.Equal("string", p.Type); Assert.False(p.IsNullable); },
            p => { Assert.Equal("Price", p.Name); Assert.Equal("decimal", p.Type); },
            p => { Assert.Equal("Notes", p.Name); Assert.True(p.IsNullable); },
            p => Assert.Equal("int", p.Type));
    }

    [Theory]
    [InlineData("qty:integer", "int")]
    [InlineData("externalId:uuid", "Guid")]
    [InlineData("total:money", "decimal")]
    [InlineData("flag:boolean", "bool")]
    [InlineData("when:datetime", "DateTime")]
    public void Maps_friendly_type_aliases(string spec, string expected) =>
        Assert.Equal(expected, PropertyParser.Parse(spec).Properties.Single().Type);

    [Fact]
    public void Passes_custom_types_through_untouched()
    {
        // Enums and value objects must survive; forge cannot know every domain type.
        var result = PropertyParser.Parse("Status:OrderStatus");
        Assert.True(result.Ok);
        Assert.Equal("OrderStatus", result.Properties.Single().Type);
    }

    /// <summary>
    /// Non-nullable strings need an initializer or every generated entity warns CS8618 under
    /// nullable reference types — and the solution builds with warnings-as-signal.
    /// </summary>
    [Fact]
    public void Non_nullable_string_gets_an_initializer_but_others_do_not()
    {
        var props = PropertyParser.Parse("Name:string,Notes:string?,Price:decimal").Properties;

        Assert.Equal("public string Name { get; set; } = string.Empty;", props[0].Render());
        Assert.Equal("public string? Notes { get; set; }", props[1].Render());
        Assert.Equal("public decimal Price { get; set; }", props[2].Render());
    }

    [Theory]
    [InlineData("Name", "not a valid property")]           // missing type
    [InlineData("Name:string,Name:int", "more than once")] // duplicate
    [InlineData("Id:Guid", "already declared")]            // collides with the template's Id
    [InlineData("9Bad:string", "not a valid C# property")] // illegal identifier
    [InlineData("Name:", "not a valid property")]          // empty type
    public void Rejects_malformed_input(string spec, string expectedFragment)
    {
        var result = PropertyParser.Parse(spec);

        Assert.False(result.Ok);
        Assert.Contains(expectedFragment, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_spec_is_valid_and_yields_no_properties()
    {
        Assert.True(PropertyParser.Parse(null).Ok);
        Assert.Empty(PropertyParser.Parse("  ").Properties);
    }
}
