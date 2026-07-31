namespace Forge.Cli.Cli;

/// <summary>
/// Documented exit-code contract. CI depends on these, so they are a compatibility
/// surface: add new codes, never repurpose an existing one.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int UsageError = 2;
    public const int ConfigInvalid = 3;

    /// <summary>
    /// Target already exists and --force was not passed. Deliberately distinct from
    /// <see cref="Error"/>: re-running a generator is an expected, benign outcome and a
    /// pipeline must be able to tell it apart from a real failure without parsing stdout.
    /// </summary>
    public const int TargetExists = 4;

    /// <summary>No stable syntax anchor could be found; the file patch was aborted untouched.</summary>
    public const int AnchorNotFound = 5;

    /// <summary>Refused by an environment guard (e.g. db:fresh or invoke:run against Production).</summary>
    public const int EnvironmentGuard = 6;
}
