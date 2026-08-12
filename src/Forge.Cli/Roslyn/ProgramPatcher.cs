using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Cli.Roslyn;

/// <summary>
/// Patches a top-level-statements Program.cs.
///
/// Different shape from every other patcher here: there is no type to anchor inside. Top-level
/// statements are GlobalStatementSyntax members of the compilation unit, so anchors are
/// statements matched by content — "the one that builds the host" — rather than members matched
/// by name.
///
/// Every edit is additive and idempotent, so running the command twice is a no-op and a
/// hand-edited Program.cs is never rewritten.
/// </summary>
public static class ProgramPatcher
{
    /// <summary>
    /// Inserts <paramref name="statements"/> after the first top-level statement whose text
    /// contains <paramref name="anchorText"/>.
    ///
    /// <paramref name="idempotencyMarker"/> is checked against the whole file first: if it is
    /// already there the file is left exactly as it is.
    /// </summary>
    public static PatchOutcome InsertAfterStatement(
        string source,
        string anchorText,
        string idempotencyMarker,
        IReadOnlyList<string> statements,
        string newLine,
        string description)
    {
        if (source.Contains(idempotencyMarker, StringComparison.Ordinal))
            return new PatchOutcome.AlreadyPresent($"{description} is already present");

        var root = SyntaxPatcher.Parse(source);

        var anchor = root.Members
            .OfType<GlobalStatementSyntax>()
            .FirstOrDefault(s => s.Statement.ToString().Contains(anchorText, StringComparison.Ordinal));

        if (anchor is null)
        {
            return new PatchOutcome.Failed(
                $"Could not find a top-level statement containing '{anchorText}', " +
                $"so forge cannot place {description}." + Environment.NewLine +
                $"  -> Add it by hand, or run with --dry-run to see the intended change.");
        }

        var indent = TriviaPreserver.IndentOf(anchor);

        var inserted = statements
            .Select(text => Build(text, indent, newLine))
            .ToList();

        var updated = root.InsertNodesAfter(anchor, inserted);
        return new PatchOutcome.Patched(updated.ToFullString());
    }

    /// <summary>
    /// Inserts before the first statement containing <paramref name="anchorText"/> — used when
    /// the new code must run BEFORE something, such as registrations before the host is built.
    /// </summary>
    public static PatchOutcome InsertBeforeStatement(
        string source,
        string anchorText,
        string idempotencyMarker,
        IReadOnlyList<string> statements,
        string newLine,
        string description)
    {
        if (source.Contains(idempotencyMarker, StringComparison.Ordinal))
            return new PatchOutcome.AlreadyPresent($"{description} is already present");

        var root = SyntaxPatcher.Parse(source);

        var anchor = root.Members
            .OfType<GlobalStatementSyntax>()
            .FirstOrDefault(s => s.Statement.ToString().Contains(anchorText, StringComparison.Ordinal));

        if (anchor is null)
        {
            return new PatchOutcome.Failed(
                $"Could not find a top-level statement containing '{anchorText}', " +
                $"so forge cannot place {description}." + Environment.NewLine +
                $"  -> Add it by hand, or run with --dry-run to see the intended change.");
        }

        var indent = TriviaPreserver.IndentOf(anchor);
        var inserted = statements.Select(text => Build(text, indent, newLine)).ToList();

        var updated = root.InsertNodesBefore(anchor, inserted);
        return new PatchOutcome.Patched(updated.ToFullString());
    }

    public static PatchOutcome EnsureUsings(
        string source,
        IReadOnlyList<string> namespaces,
        string newLine)
    {
        var root = SyntaxPatcher.Parse(source);
        var before = root.ToFullString();

        foreach (var ns in namespaces) root = SyntaxPatcher.EnsureUsing(root, ns, newLine);

        var after = root.ToFullString();
        return after == before
            ? new PatchOutcome.AlreadyPresent("using directives already present")
            : new PatchOutcome.Patched(after);
    }

    /// <summary>
    /// Parses text into a global statement carrying its own leading blank line and trailing
    /// newline, so an inserted block reads as a block rather than being jammed against its
    /// neighbour. Multi-line text is emitted verbatim — it is forge's own, already formatted.
    /// </summary>
    private static GlobalStatementSyntax Build(string text, string indent, string newLine)
    {
        var statement = SyntaxFactory.ParseStatement(text)
            ?? throw new InvalidOperationException($"forge produced an unparseable statement: {text}");

        return SyntaxFactory.GlobalStatement(statement)
            .WithLeadingTrivia(
                SyntaxFactory.EndOfLine(newLine),
                SyntaxFactory.Whitespace(indent))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));
    }
}
