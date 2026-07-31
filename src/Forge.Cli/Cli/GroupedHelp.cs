using System.CommandLine;

namespace Forge.Cli.Cli;

/// <summary>
/// Colon-named commands register FLAT — "make" is not a parent command, it is the first half
/// of a string — so System.CommandLine lists them all at the root with no grouping. This
/// rebuilds the grouping by splitting each name on ':'. A command appears here automatically
/// as long as it has a description and a colon prefix.
/// </summary>
public static class GroupedHelp
{
    public static void Render(RootCommand root, Output output)
    {
        var commands = root.Subcommands
            .Where(c => !c.Hidden)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var grouped = commands
            .Where(c => c.Name.Contains(':'))
            .GroupBy(c => c.Name[..c.Name.IndexOf(':')])
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var ungrouped = commands.Where(c => !c.Name.Contains(':')).ToList();

        output.Info($"forge {ForgeVersion.Current} — architectural code generator for ASP.NET Core");
        output.Blank();
        output.Info("Usage: dotnet forge <namespace>:<verb> [options]");
        output.Blank();

        var width = commands.Count == 0 ? 0 : commands.Max(c => c.Name.Length) + 2;

        foreach (var group in grouped)
        {
            output.Info($"{group.Key}:");
            foreach (var command in group)
                output.Info($"  {command.Name.PadRight(width)}{command.Description}");
            output.Blank();
        }

        if (ungrouped.Count > 0)
        {
            output.Info("general:");
            foreach (var command in ungrouped)
                output.Info($"  {command.Name.PadRight(width)}{command.Description}");
            output.Blank();
        }

        output.Dim("Every command accepts --dry-run, --json, --force, --no-color.");
    }
}
