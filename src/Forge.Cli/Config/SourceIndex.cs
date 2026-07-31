using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Cli.Config;

/// <summary>A declared property, or a positional record parameter.</summary>
public sealed record PropertyInfo(string Name, string Type);

public sealed record TypeEntry(
    string Name,
    SyntaxKind Kind,
    string FilePath,
    string? Namespace,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> Members,
    IReadOnlyList<PropertyInfo> Properties);

/// <summary>
/// One syntactic pass over every .cs file under the solution root, indexed by type name.
/// Used by forge init to infer configuration from what is actually in the codebase rather than
/// from folder-name guesswork alone.
///
/// Purely syntactic — no Compilation, no SemanticModel. See CLAUDE.md on why that matters.
/// </summary>
public sealed class SourceIndex
{
    private readonly List<TypeEntry> _types = [];

    public IReadOnlyList<TypeEntry> Types => _types;

    public static SourceIndex Build(string root, int maxFiles = 5000)
    {
        var index = new SourceIndex();

        var files = Fs.Files(root, "*.cs")
            .Where(f => !IsGenerated(f))
            .Take(maxFiles);

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            SyntaxNode root_;
            try { root_ = CSharpSyntaxTree.ParseText(text).GetRoot(); }
            catch { continue; }

            foreach (var type in root_.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                index._types.Add(new TypeEntry(
                    type.Identifier.Text,
                    type.Kind(),
                    file,
                    NamespaceOf(type),
                    type.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
                    // Enum members let make:feature validate --roles against the real enum
                    // instead of generating code that references a role that does not exist.
                    type is EnumDeclarationSyntax e
                        ? e.Members.Select(m => m.Identifier.Text).ToList()
                        : [],
                    PropertiesOf(type)));
            }
        }

        return index;
    }

    private static bool IsGenerated(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Public instance properties, plus positional record parameters — so a response can be
    /// derived from an entity whether it is a class or a positional record.
    /// </summary>
    private static IReadOnlyList<PropertyInfo> PropertiesOf(BaseTypeDeclarationSyntax type)
    {
        var result = new List<PropertyInfo>();

        if (type is TypeDeclarationSyntax declaration)
        {
            if (declaration.ParameterList is not null)
            {
                foreach (var parameter in declaration.ParameterList.Parameters)
                {
                    if (parameter.Type is not null)
                        result.Add(new PropertyInfo(parameter.Identifier.Text, parameter.Type.ToString()));
                }
            }

            foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                var isPublic = property.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                var isStatic = property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                if (isPublic && !isStatic)
                    result.Add(new PropertyInfo(property.Identifier.Text, property.Type.ToString()));
            }
        }

        return result;
    }

    private static string? NamespaceOf(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is FileScopedNamespaceDeclarationSyntax fs) return fs.Name.ToString();
            if (current is NamespaceDeclarationSyntax ns) return ns.Name.ToString();
        }
        return null;
    }

    public TypeEntry? FindType(string name, SyntaxKind kind) =>
        _types.FirstOrDefault(t => t.Name == name && t.Kind == kind);

    public IEnumerable<TypeEntry> Interfaces(Func<string, bool> namePredicate) =>
        _types.Where(t => t.Kind == SyntaxKind.InterfaceDeclaration && namePredicate(t.Name));

    public IEnumerable<TypeEntry> Classes(Func<string, bool> namePredicate) =>
        _types.Where(t => t.Kind == SyntaxKind.ClassDeclaration && namePredicate(t.Name));

    public TypeEntry? FirstDerivedFrom(string baseTypeSuffix) =>
        _types.FirstOrDefault(t => t.BaseTypes.Any(b =>
            b.EndsWith(baseTypeSuffix, StringComparison.Ordinal)));
}
