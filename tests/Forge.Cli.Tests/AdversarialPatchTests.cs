using Forge.Cli.Planning;
using Forge.Cli.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Cli.Tests;

/// <summary>
/// The highest-value tests in the project. Each fixture is a hostile-but-legal shape that
/// really occurs in real repositories. The tool must either patch it correctly or refuse
/// cleanly — a clean refusal is a PASS. Silently producing a subtly wrong patch is the failure
/// mode these exist to prevent.
/// </summary>
[Trait("Category", "Roslyn")]
public class AdversarialPatchTests
{
    private const string NewLine = "\r\n";
    private const string Indent = "    ";

    public static TheoryData<string> InterfaceFixtures() => Fixtures("IUnitOfWork");
    public static TheoryData<string> ClassFixtures() => Fixtures("UnitOfWork");
    public static TheoryData<string> DbContextFixtures() => Fixtures("DbContext");

    [Theory]
    [MemberData(nameof(DbContextFixtures))]
    public void DbSet_patch_never_corrupts(string name)
    {
        var before = ReadFixture("DbContext", name);
        var outcome = SyntaxPatcher.AddDbSetToContext(
            before, "MyShopDbContext", "Product", "Products",
            "MyShop.Domain.Entities", NewLine, Indent);

        if (outcome is PatchOutcome.Failed failed)
        {
            Assert.False(string.IsNullOrWhiteSpace(failed.Error), "A refusal must explain itself.");
            return;
        }

        var patched = Assert.IsType<PatchOutcome.Patched>(outcome);
        AssertPatchIsSound(before, patched.After, "Products");

        Assert.Contains("DbSet<Product> Products => Set<Product>();", patched.After);
        // The entity namespace must come along, or the patched file will not compile.
        Assert.Contains("using MyShop.Domain.Entities;", patched.After);

        var second = SyntaxPatcher.AddDbSetToContext(
            patched.After, "MyShopDbContext", "Product", "Products",
            "MyShop.Domain.Entities", NewLine, Indent);
        Assert.IsType<PatchOutcome.AlreadyPresent>(second);
    }

    [Theory]
    [MemberData(nameof(InterfaceFixtures))]
    public void Interface_patch_never_corrupts(string name)
    {
        var before = ReadFixture("IUnitOfWork", name);
        var outcome = SyntaxPatcher.AddRepositoryToInterface(
            before, "IUnitOfWork", "Gig", "IGigRepository", NewLine, Indent);

        switch (outcome)
        {
            case PatchOutcome.AlreadyPresent:
                // Idempotency fixture — correct behaviour.
                Assert.Equal("member-already-present", name);
                return;

            case PatchOutcome.Failed failed:
                Assert.False(string.IsNullOrWhiteSpace(failed.Error), "A refusal must explain itself.");
                return;

            case PatchOutcome.Patched patched:
                AssertPatchIsSound(before, patched.After, "Gig");

                // Idempotency: patching the result again must be a no-op.
                var second = SyntaxPatcher.AddRepositoryToInterface(
                    patched.After, "IUnitOfWork", "Gig", "IGigRepository", NewLine, Indent);
                Assert.IsType<PatchOutcome.AlreadyPresent>(second);
                return;
        }
    }

    [Theory]
    [MemberData(nameof(ClassFixtures))]
    public void Class_patch_never_corrupts(string name)
    {
        var before = ReadFixture("UnitOfWork", name);
        var outcome = SyntaxPatcher.AddRepositoryToUnitOfWork(
            before, "UnitOfWork", "Gig", "IGigRepository", "gigRepository", NewLine, Indent);

        if (outcome is PatchOutcome.Failed failed)
        {
            // A primary-constructor UnitOfWork is a documented, deliberate refusal.
            Assert.Equal("primary-constructor", name);
            Assert.Contains("primary-constructor", failed.Error);
            return;
        }

        var patched = Assert.IsType<PatchOutcome.Patched>(outcome);
        AssertPatchIsSound(before, patched.After, "Gig");

        // The constructor must have gained both a parameter and its assignment.
        Assert.Contains("IGigRepository gigRepository", patched.After);
        Assert.Contains("Gig = gigRepository;", patched.After);

        var second = SyntaxPatcher.AddRepositoryToUnitOfWork(
            patched.After, "UnitOfWork", "Gig", "IGigRepository", "gigRepository", NewLine, Indent);
        Assert.IsType<PatchOutcome.AlreadyPresent>(second);
    }

    /// <summary>
    /// The shared contract every successful patch must satisfy.
    /// </summary>
    private static void AssertPatchIsSound(string before, string after, string memberName)
    {
        // 1. Still parses with zero syntax errors.
        var tree = CSharpSyntaxTree.ParseText(after);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"Patched output has syntax errors: {string.Join("; ", errors.Select(e => e.GetMessage()))}");

        // 2. The member landed exactly once.
        var count = tree.GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Count(p => p.Identifier.Text == memberName);
        Assert.Equal(1, count);

        // 3. NO TOKEN IS EVER LOST OR REORDERED — the original token stream must still be a
        //    subsequence of the new one. Line-level "additions only" is the wrong invariant
        //    here: adding a constructor parameter legitimately extends an existing line.
        //    This catches deletion and reordering without false-flagging extension.
        var beforeTokens = CodeTokens(before);
        var afterTokens = CodeTokens(after);
        Assert.True(IsSubsequence(beforeTokens, afterTokens),
            "Patch lost or reordered existing code:\n" + LineDiff.Render(before, after));

        // 4. COMMENT AND REGION TRIVIA COUNTS ARE UNCHANGED. This is the assertion that catches
        //    the WithTriviaFrom bug — a duplicated XML doc comment or an orphaned #region shows
        //    up here and nowhere else, least of all in a "does it still compile" check.
        Assert.Equal(SignificantTrivia(before), SignificantTrivia(after));
    }

    private static List<string> CodeTokens(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantTokens()
            .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken))
            .Select(t => t.Text)
            .ToList();

    private static bool IsSubsequence(List<string> needle, List<string> haystack)
    {
        var i = 0;
        foreach (var token in haystack)
        {
            if (i < needle.Count && needle[i] == token) i++;
        }
        return i == needle.Count;
    }

    private static Dictionary<string, int> SignificantTrivia(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                     || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.RegionDirectiveTrivia)
                     || t.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            .GroupBy(t => t.ToString().Trim())
            .ToDictionary(g => g.Key, g => g.Count());

    private static string ReadFixture(string group, string name) =>
        File.ReadAllText(Path.Combine(FixtureDir(group), name + ".txt"));

    private static TheoryData<string> Fixtures(string group)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FixtureDir(group), "*.txt").OrderBy(f => f))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    private static string FixtureDir(string group) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Adversarial", group);
}
