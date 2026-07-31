using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Forge.Cli.Roslyn.MemberNames;

namespace Forge.Cli.Roslyn;

/// <summary>
/// Non-destructive edits to existing C# source.
///
/// Named SyntaxPatcher rather than SyntaxEditor deliberately: Microsoft.CodeAnalysis.Editing
/// already ships a SyntaxEditor type, and colliding with it would force an alias in every file
/// that touched both.
///
/// Roslyn is used here purely as a PARSER. No Compilation, no SemanticModel — those would
/// require the user's full reference closure and destroy the process isolation that lets forge
/// ship its own Roslyn without ever conflicting with the target project.
/// </summary>
public static class SyntaxPatcher
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();

    // ---------------------------------------------------------------------------------
    // using directives
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds "using {namespaceName};" if the file does not already have access to that namespace.
    ///
    /// Inserting a type reference without its using is the difference between a patch that
    /// compiles and one that does not — and the failure only shows up at build time, in a file
    /// the user did not write.
    /// </summary>
    public static CompilationUnitSyntax EnsureUsing(
        CompilationUnitSyntax root,
        string namespaceName,
        string newLine)
    {
        if (string.IsNullOrWhiteSpace(namespaceName)) return root;

        if (root.Usings.Any(u => u.Name?.ToString() == namespaceName)) return root;

        // A file declaring namespace A.B.C can already see types in A.B and A — no using needed.
        var declared = DeclaredNamespace(root);
        if (declared is not null &&
            (declared == namespaceName || declared.StartsWith(namespaceName + ".", StringComparison.Ordinal)))
        {
            return root;
        }

        var directive = SyntaxFactory
            .UsingDirective(SyntaxFactory.ParseName(namespaceName))
            // Without explicit trailing space on the keyword this emits "usingSystem.Foo;".
            .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));

        if (root.Usings.Count > 0)
        {
            // Append after the last using rather than trying to slot it in alphabetically: the
            // existing block may not be sorted, and appending is the smallest predictable diff.
            return root.InsertNodesAfter(root.Usings[^1], [directive]);
        }

        // No usings yet: place the directive above the namespace declaration, carrying over the
        // declaration's leading trivia so any file header comment stays on top.
        var member = root.Members.FirstOrDefault();
        if (member is null) return root.WithUsings(SyntaxFactory.SingletonList(directive));

        var leading = member.GetLeadingTrivia();
        return root
            .WithMembers(root.Members.Replace(member, member.WithLeadingTrivia(SyntaxFactory.EndOfLine(newLine))))
            .WithUsings(SyntaxFactory.SingletonList(directive.WithLeadingTrivia(leading)));
    }

    private static string? DeclaredNamespace(CompilationUnitSyntax root)
    {
        var fileScoped = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScoped is not null) return fileScoped.Name.ToString();

        return root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
    }

    // ---------------------------------------------------------------------------------
    // IUnitOfWork.cs — add "IGigRepository Gig { get; }" to the interface
    // ---------------------------------------------------------------------------------

    public static PatchOutcome AddRepositoryToInterface(
        string source,
        string interfaceName,
        string propertyName,
        string repositoryType,
        string newLine,
        string indentUnit,
        string? repositoryNamespace = null)
    {
        var root = Parse(source);

        var iface = root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .FirstOrDefault(i => i.Identifier.Text == interfaceName);

        if (iface is null)
            return new PatchOutcome.Failed(
                $"Could not find interface '{interfaceName}'. " +
                $"Check 'applicationUnitOfWorkInterfacePath' in forge.config.json points at the right file.");

        // Idempotency: a real check against the tree, not a text Contains — which would match
        // comments, string literals and unrelated members.
        if (iface.Members.Any(m => NameOf(m) == propertyName))
            return new PatchOutcome.AlreadyPresent($"'{propertyName}' is already a member of {interfaceName}");

        var member = SyntaxFactory.ParseMemberDeclaration($"{repositoryType} {propertyName} {{ get; }}");
        if (member is null)
            return new PatchOutcome.Failed($"Internal error: '{repositoryType} {propertyName} {{ get; }}' did not parse.");

        var (anchor, insertBefore) = FindInterfaceAnchor(iface);

        InterfaceDeclarationSyntax newIface;
        if (anchor is null)
        {
            // Empty interface — no sibling to match, so derive indent from the declaration itself.
            var indent = TriviaPreserver.IndentOf(iface) + indentUnit;
            newIface = iface.WithMembers(SyntaxFactory.SingletonList(member.AtIndent(indent, newLine)));
        }
        else
        {
            var positioned = member.AsSiblingOf(anchor, newLine);
            newIface = insertBefore
                ? iface.InsertNodesBefore(anchor, [positioned])
                : iface.InsertNodesAfter(anchor, [positioned]);
        }

        // ToFullString, never NormalizeWhitespace — the diff must be the new member only,
        // not a reflow of the entire file.
        var updated = root.ReplaceNode(iface, newIface);
        updated = EnsureUsing(updated, repositoryNamespace ?? string.Empty, newLine);
        return new PatchOutcome.Patched(updated.ToFullString());
    }

    /// <summary>
    /// Anchor chain. Note the first query walks MemberDeclarationSyntax, not properties:
    /// SaveChangesAsync is a METHOD in every IUnitOfWork this tool targets, so filtering to
    /// properties first would drop it before the name predicate ever ran.
    /// </summary>
    private static (MemberDeclarationSyntax? Anchor, bool InsertBefore) FindInterfaceAnchor(
        InterfaceDeclarationSyntax iface)
    {
        var saveChanges = iface.Members.FirstOrDefault(m => NameOf(m) == "SaveChangesAsync");
        if (saveChanges is not null) return (saveChanges, true);

        var lastRepo = iface.Members
            .OfType<PropertyDeclarationSyntax>()
            .LastOrDefault(p => IsRepositoryType(p.Type));
        if (lastRepo is not null) return (lastRepo, false);

        return (iface.Members.LastOrDefault(), false);
    }

    // ---------------------------------------------------------------------------------
    // UnitOfWork.cs — three coordinated edits: property, ctor parameter, ctor assignment
    // ---------------------------------------------------------------------------------

    public static PatchOutcome AddRepositoryToUnitOfWork(
        string source,
        string className,
        string propertyName,
        string repositoryType,
        string parameterName,
        string newLine,
        string indentUnit,
        string? repositoryNamespace = null)
    {
        var root = Parse(source);

        var cls = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);

        if (cls is null)
            return new PatchOutcome.Failed(
                $"Could not find class '{className}'. " +
                $"Check 'infrastructureUnitOfWorkImplPath' in forge.config.json points at the right file.");

        if (cls.Members.Any(m => NameOf(m) == propertyName))
            return new PatchOutcome.AlreadyPresent($"'{propertyName}' is already a member of {className}");

        // ---- Resolve EVERY anchor before computing any edit. If one is missing the whole
        // ---- file patch aborts, so there is never a partially-patched file to hand-fix.
        var ctor = cls.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        if (ctor is null)
        {
            var hint = cls.ParameterList is not null
                ? $"'{className}' uses primary-constructor syntax, which forge cannot patch yet."
                : $"'{className}' has no constructor to add a dependency to.";

            return new PatchOutcome.Failed(
                $"{hint}{Environment.NewLine}" +
                $"  -> Add '{repositoryType} {propertyName}' manually, or re-run with --dry-run to see the intended change.");
        }

        if (ctor.Body is null)
            return new PatchOutcome.Failed(
                $"The '{className}' constructor is expression-bodied, so there is no statement list to " +
                $"add an assignment to.{Environment.NewLine}  -> Convert it to a block body, or add the property manually.");

        var assignAnchor = ctor.Body.Statements
            .OfType<ExpressionStatementSyntax>()
            .LastOrDefault(s => s.Expression is AssignmentExpressionSyntax);

        if (assignAnchor is null && ctor.Body.Statements.Count > 0)
            return new PatchOutcome.Failed(
                $"Could not find a field assignment in the '{className}' constructor to anchor against." +
                $"{Environment.NewLine}  -> Add '{propertyName} = {parameterName};' manually.");

        // ---- All anchors resolved. Now build the edits.

        var propertyText = $"public {repositoryType} {propertyName} {{ get; }}";
        var property = SyntaxFactory.ParseMemberDeclaration(propertyText);
        if (property is null)
            return new PatchOutcome.Failed($"Internal error: '{propertyText}' did not parse.");

        var propertyAnchor = FindPropertyAnchor(cls);

        ClassDeclarationSyntax updated;
        if (propertyAnchor is null)
        {
            var indent = TriviaPreserver.IndentOf(cls) + indentUnit;
            updated = cls.WithMembers(cls.Members.Insert(0, property.AtIndent(indent, newLine)));
        }
        else
        {
            updated = cls.InsertNodesAfter(propertyAnchor, [property.AsSiblingOf(propertyAnchor, newLine)]);
        }

        // Re-find the constructor inside the updated class: Roslyn nodes are immutable, so the
        // original `ctor` reference no longer belongs to this tree.
        var liveCtor = updated.Members.OfType<ConstructorDeclarationSyntax>().First(c =>
            c.Identifier.Text == ctor.Identifier.Text &&
            c.ParameterList.Parameters.Count == ctor.ParameterList.Parameters.Count);

        var newCtor = AddDependency(liveCtor, repositoryType, parameterName, propertyName, newLine, indentUnit);
        updated = updated.ReplaceNode(liveCtor, newCtor);

        var result = root.ReplaceNode(cls, updated);
        result = EnsureUsing(result, repositoryNamespace ?? string.Empty, newLine);
        return new PatchOutcome.Patched(result.ToFullString());
    }

    private static PropertyDeclarationSyntax? FindPropertyAnchor(ClassDeclarationSyntax cls)
    {
        var properties = cls.Members.OfType<PropertyDeclarationSyntax>().ToList();
        return properties.LastOrDefault(p => IsRepositoryType(p.Type)) ?? properties.LastOrDefault();
    }

    /// <summary>Adds the constructor parameter and its matching assignment in one rewrite.</summary>
    private static ConstructorDeclarationSyntax AddDependency(
        ConstructorDeclarationSyntax ctor,
        string type,
        string parameterName,
        string propertyName,
        string newLine,
        string indentUnit)
    {
        // --- parameter
        var parameters = ctor.ParameterList.Parameters;
        // ToString(), not ToFullString(): the latter includes trailing trivia — i.e. the newline
        // AFTER the closing paren — so every single-line parameter list would look wrapped.
        var multiLine = ctor.ParameterList.ToString().Contains('\n');

        var parameter = SyntaxFactory
            .Parameter(SyntaxFactory.Identifier(parameterName))
            .WithType(SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space));

        if (multiLine && parameters.Count > 0)
        {
            // Keep a wrapped parameter list wrapped, matching the last parameter's indent.
            var indent = TriviaPreserver.IndentOf(parameters[^1]);
            parameter = parameter.WithLeadingTrivia(
                SyntaxFactory.EndOfLine(newLine),
                SyntaxFactory.Whitespace(indent.Length > 0 ? indent : indentUnit + indentUnit));
        }
        else if (parameters.Count > 0)
        {
            parameter = parameter.WithLeadingTrivia(SyntaxFactory.Space);
        }

        var newCtor = ctor.WithParameterList(ctor.ParameterList.AddParameters(parameter));

        // --- assignment
        var body = newCtor.Body!;
        var statement = SyntaxFactory.ParseStatement($"{propertyName} = {parameterName};");

        var anchor = body.Statements
            .OfType<ExpressionStatementSyntax>()
            .LastOrDefault(s => s.Expression is AssignmentExpressionSyntax);

        if (anchor is not null)
        {
            var index = body.Statements.IndexOf(anchor);

            // A single-line body — "{ Artists = artists; }" — must stay on one line. Giving the
            // inserted statement a trailing newline would push the closing brace onto its own
            // line: valid C#, but a needlessly noisy diff.
            statement = body.ToString().Contains('\n')
                ? statement.AsSiblingOf(anchor, newLine)
                : statement.WithLeadingTrivia(SyntaxFactory.ElasticMarker)
                           .WithTrailingTrivia(SyntaxFactory.Space);

            return newCtor.WithBody(body.WithStatements(body.Statements.Insert(index + 1, statement)));
        }

        var fallbackIndent = TriviaPreserver.IndentOf(newCtor) + indentUnit;
        statement = statement.AtIndent(fallbackIndent, newLine);
        return newCtor.WithBody(body.WithStatements(body.Statements.Insert(0, statement)));
    }
}
