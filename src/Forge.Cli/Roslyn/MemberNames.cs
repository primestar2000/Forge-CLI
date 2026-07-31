using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Cli.Roslyn;

/// <summary>
/// There is no common Identifier property on MemberDeclarationSyntax, so name lookup has to
/// switch on member kind. Doing this in one place is what stops the classic bug of filtering
/// to OfType&lt;PropertyDeclarationSyntax&gt;() and then matching a name that belongs to a method.
/// </summary>
public static class MemberNames
{
    public static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        PropertyDeclarationSyntax p => p.Identifier.Text,
        MethodDeclarationSyntax m => m.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        _ => null
    };

    /// <summary>True for types like IGigRepository / IUserRepository.</summary>
    public static bool IsRepositoryType(TypeSyntax type)
    {
        var name = type.ToString();
        return name.Length > 1
            && name[0] == 'I'
            && char.IsUpper(name.Length > 1 ? name[1] : 'a')
            && name.EndsWith("Repository", StringComparison.Ordinal);
    }
}
