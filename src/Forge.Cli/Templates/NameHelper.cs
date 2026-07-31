using Humanizer;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Templates;

/// <summary>
/// All naming derivation lives here so every generator agrees. Ad-hoc string manipulation
/// inlined in a generator is how "Categorys" and "Peoples" get shipped.
/// </summary>
public static class NameHelper
{
    /// <summary>Gig, gig, GIG -> Gig. Strips a leading I from interface-looking input.</summary>
    public static string Pascal(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    public static string Camel(string raw)
    {
        var pascal = Pascal(raw);
        if (pascal.Length == 0) return pascal;
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>Humanizer, not "+ s" — English pluralisation is irregular (Person/People).</summary>
    public static string Plural(string raw) => Pascal(raw).Pluralize(inputIsKnownToBeSingular: false);

    /// <summary>Constructor parameter name for an injected repository: Gig -> gigRepository.</summary>
    public static string RepositoryParameter(string entity) => Camel(entity) + "Repository";

    public static string RepositoryInterface(string entity) => "I" + Pascal(entity) + "Repository";

    public static string RepositoryClass(string entity) => Pascal(entity) + "Repository";

    /// <summary>
    /// Rejects input that is not a legal C# identifier BEFORE any planning happens, so the
    /// failure is a clean usage error rather than a confusing error halfway through a plan.
    /// </summary>
    public static bool IsValidIdentifier(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        SyntaxFacts.IsValidIdentifier(name) &&
        !SyntaxFacts.GetKeywordKind(name).ToString().EndsWith("Keyword", StringComparison.Ordinal);
}
