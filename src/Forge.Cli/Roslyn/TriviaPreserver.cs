using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Roslyn;

/// <summary>
/// Matches an inserted node's indentation to its neighbour without reformatting anything.
///
/// Deliberately does NOT use WithTriviaFrom: that copies the anchor's entire leading trivia,
/// so an anchor carrying an XML doc comment or a #region directive would have that trivia
/// duplicated onto the new member — producing a second copy of the doc comment, or an orphaned
/// #region that breaks compilation. Only the indentation whitespace is ever cloned.
/// </summary>
public static class TriviaPreserver
{
    /// <summary>The indentation whitespace immediately preceding <paramref name="anchor"/>.</summary>
    public static string IndentOf(SyntaxNode anchor)
    {
        var whitespace = anchor.GetLeadingTrivia()
            .LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));

        return whitespace == default ? string.Empty : whitespace.ToString();
    }

    /// <summary>
    /// Positions <paramref name="node"/> as a sibling of <paramref name="anchor"/>: same indent,
    /// terminated by a single newline. No other trivia crosses over.
    /// </summary>
    public static T AsSiblingOf<T>(this T node, SyntaxNode anchor, string newLine) where T : SyntaxNode =>
        node.WithLeadingTrivia(SyntaxFactory.Whitespace(IndentOf(anchor)))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));

    /// <summary>For the first member of an otherwise empty type, where there is no sibling to match.</summary>
    public static T AtIndent<T>(this T node, string indent, string newLine) where T : SyntaxNode =>
        node.WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));
}
