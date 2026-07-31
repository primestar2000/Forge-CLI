using System.CommandLine;

namespace Forge.Cli.Cli;

/// <summary>
/// Flags every command carries. Defined once and attached via <see cref="AddTo"/> so a new
/// command physically cannot forget them — these are global, not just make:*/db:*.
/// </summary>
public static class GlobalOptions
{
    public static readonly Option<bool> Force =
        new("--force") { Description = "Overwrite existing files instead of refusing." };

    public static readonly Option<bool> DryRun =
        new("--dry-run") { Description = "Show the diff of what would change; write nothing." };

    public static readonly Option<bool> Json =
        new("--json") { Description = "Emit machine-readable JSON instead of the human summary." };

    public static readonly Option<bool> NoColor =
        new("--no-color") { Description = "Suppress ANSI colour (NO_COLOR is also honoured)." };

    public static readonly Option<string> Verbosity =
        new("--verbosity", "-v") { Description = "Output detail: q[uiet], m[inimal], d[etailed]." };

    public static IReadOnlyList<Option> All => [Force, DryRun, Json, NoColor, Verbosity];

    public static Command WithGlobals(this Command command)
    {
        foreach (var option in All) command.Options.Add(option);
        return command;
    }
}
