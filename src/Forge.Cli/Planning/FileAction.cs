namespace Forge.Cli.Planning;

/// <summary>
/// One intended change to one file. Actions are pure data — producing one never touches
/// the filesystem. Only <see cref="PlanExecutor"/> may act on them.
/// </summary>
public abstract record FileAction(string Path)
{
    /// <summary>Create a brand-new file.</summary>
    public sealed record Create(string Path, string Content) : FileAction(Path);

    /// <summary>
    /// Patch an existing file. Both texts are fully computed at planning time, which is what
    /// makes --dry-run byte-for-byte honest rather than an approximation.
    /// </summary>
    public sealed record Patch(string Path, string Before, string After) : FileAction(Path);

    /// <summary>
    /// Nothing to do, with a human-readable reason (already exists, already wired, ...).
    ///
    /// <paramref name="Protected"/> marks the specific case where forge REFUSED a write the user
    /// asked for, because the target had been edited since generation. That is materially
    /// different from an ordinary "already exists" skip and is surfaced as exit code 4 so a
    /// pipeline notices rather than assuming the overwrite happened.
    /// </summary>
    public sealed record Skip(string Path, string Reason, bool Protected = false) : FileAction(Path);

    public string Kind => this switch
    {
        Create => "create",
        Patch => "patch",
        Skip => "skip",
        _ => "unknown"
    };
}
