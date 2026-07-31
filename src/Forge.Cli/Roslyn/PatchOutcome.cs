namespace Forge.Cli.Roslyn;

/// <summary>
/// The result of attempting a patch. A clean refusal (<see cref="Failed"/>) is a legitimate,
/// well-behaved outcome — far better than silently producing a subtly wrong edit.
/// </summary>
public abstract record PatchOutcome
{
    /// <summary>The edit was computed. <paramref name="After"/> is the full new file text.</summary>
    public sealed record Patched(string After) : PatchOutcome;

    /// <summary>The target member is already there. Idempotent no-op, not an error.</summary>
    public sealed record AlreadyPresent(string Reason) : PatchOutcome;

    /// <summary>No stable anchor could be found. Nothing should be written.</summary>
    public sealed record Failed(string Error) : PatchOutcome;
}
