namespace Forge.Cli.Planning;

/// <summary>
/// The complete set of changes a command intends to make. Generators return one of these
/// instead of writing files, which is what makes --dry-run, --json, command-level atomic
/// rollback and filesystem-free testing all fall out of a single mechanism.
/// </summary>
public sealed record GenerationPlan(IReadOnlyList<FileAction> Actions)
{
    public static GenerationPlan Empty { get; } = new(Array.Empty<FileAction>());

    public static GenerationPlan Of(params FileAction[] actions) => new(actions);

    public GenerationPlan With(params FileAction[] actions) =>
        new([.. Actions, .. actions]);

    public GenerationPlan Concat(GenerationPlan other) =>
        other.Actions.Count == 0 ? this : new([.. Actions, .. other.Actions]);

    public bool IsEmpty => Actions.Count == 0;

    /// <summary>True when every action is a Skip — i.e. the command was a complete no-op.</summary>
    public bool IsEntirelySkipped => Actions.Count > 0 && Actions.All(a => a is FileAction.Skip);

    public IEnumerable<FileAction> Writes =>
        Actions.Where(a => a is FileAction.Create or FileAction.Patch);

    /// <summary>
    /// Two actions writing the same path is a composition bug (e.g. make:feature --with-repo
    /// where both halves emit the same file). Caught before anything is written.
    /// </summary>
    public IReadOnlyList<string> ConflictingPaths =>
        Writes.GroupBy(a => System.IO.Path.GetFullPath(a.Path), StringComparer.OrdinalIgnoreCase)
              .Where(g => g.Count() > 1)
              .Select(g => g.Key)
              .ToList();
}
