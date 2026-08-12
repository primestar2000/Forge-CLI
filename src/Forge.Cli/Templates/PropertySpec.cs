using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Templates;

/// <summary>What make:entity was asked to build.</summary>
/// <param name="Encapsulated">
/// Private setters plus a generated constructor and Update method, rather than public setters.
/// The default, because this template is an onion/DDD template and a freely mutable entity
/// contradicts the architecture it promises. <c>--public-setters</c> opts out.
/// </param>
public sealed record EntitySpec(
    string Name,
    IReadOnlyList<PropertySpec> Properties,
    bool SkipDbSet = false,
    bool Encapsulated = true);

public sealed record PropertySpec(string Name, string Type, bool IsNullable)
{
    public string DeclaredType => IsNullable ? Type + "?" : Type;

    /// <summary>
    /// Non-nullable reference types need an initializer or the compiler warns CS8618 on every
    /// generated entity. Value types and nullable types do not.
    ///
    /// This carries the encapsulated shape too: the EF materialisation constructor is
    /// parameterless, so without an initializer every non-nullable string warns there as well.
    /// </summary>
    public string? Initializer => (IsNullable, Type) switch
    {
        (true, _) => null,
        (_, "string") => "string.Empty",
        _ => null
    };

    /// <summary>
    /// The property as a constructor or method parameter: camelCased, and escaped with @ when
    /// that collides with a keyword. An entity with a property named Event or Class would
    /// otherwise generate a constructor that does not parse.
    /// </summary>
    public string ParameterName
    {
        get
        {
            var camel = Name.Length == 0
                ? Name
                : char.ToLowerInvariant(Name[0]) + Name[1..];

            return SyntaxFacts.GetKeywordKind(camel) == SyntaxKind.None
                   && SyntaxFacts.GetContextualKeywordKind(camel) == SyntaxKind.None
                ? camel
                : "@" + camel;
        }
    }

    public string Parameter => $"{DeclaredType} {ParameterName}";

    /// <summary>Assignment inside a constructor or Update, with @ stripped from the target.</summary>
    public string Assignment => $"{Name} = {ParameterName};";

    public string Render(bool encapsulated = false)
    {
        var setter = encapsulated ? "get; private set;" : "get; set;";

        return Initializer is null
            ? $"public {DeclaredType} {Name} {{ {setter} }}"
            : $"public {DeclaredType} {Name} {{ {setter} }} = {Initializer};";
    }
}

public sealed record PropertyParseResult(IReadOnlyList<PropertySpec> Properties, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Parses --properties "Name:string,Price:decimal,Notes:string?" into real declarations.
/// </summary>
public static class PropertyParser
{
    /// <summary>Friendly aliases so "String"/"Int32"/"bool" all work.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = "string",
        ["str"] = "string",
        ["int"] = "int",
        ["integer"] = "int",
        ["int32"] = "int",
        ["long"] = "long",
        ["int64"] = "long",
        ["short"] = "short",
        ["byte"] = "byte",
        ["decimal"] = "decimal",
        ["money"] = "decimal",
        ["double"] = "double",
        ["float"] = "float",
        ["bool"] = "bool",
        ["boolean"] = "bool",
        ["guid"] = "Guid",
        ["uuid"] = "Guid",
        ["datetime"] = "DateTime",
        ["datetimeoffset"] = "DateTimeOffset",
        ["dateonly"] = "DateOnly",
        ["timeonly"] = "TimeOnly",
        ["timespan"] = "TimeSpan",
        ["char"] = "char"
    };

    /// <summary>Reserved because the entity template already declares them.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "Id" };

    public static PropertyParseResult Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return new PropertyParseResult([], null);

        var properties = new List<PropertySpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return Fail($"'{entry}' is not a valid property. Expected Name:type, e.g. Price:decimal.");
            }

            var name = NameHelper.Pascal(parts[0]);
            if (!NameHelper.IsValidIdentifier(name))
                return Fail($"'{parts[0]}' is not a valid C# property name.");

            if (Reserved.Contains(name))
                return Fail($"'{name}' is already declared by the entity template - remove it from --properties.");

            if (!seen.Add(name))
                return Fail($"Property '{name}' is specified more than once.");

            var typeText = parts[1];
            var nullable = typeText.EndsWith('?');
            if (nullable) typeText = typeText[..^1].TrimEnd();

            if (typeText.Length == 0)
                return Fail($"'{entry}' has no type.");

            // Known alias, otherwise pass a custom type through untouched (enums, value objects).
            if (Aliases.TryGetValue(typeText, out var mapped))
            {
                typeText = mapped;
            }
            else if (!SyntaxFacts.IsValidIdentifier(typeText))
            {
                return Fail($"'{parts[1]}' is not a recognised type or valid type name.");
            }

            properties.Add(new PropertySpec(name, typeText, nullable));
        }

        return new PropertyParseResult(properties, null);
    }

    private static PropertyParseResult Fail(string error) => new([], error);
}
