using System.Diagnostics;
using Forge.Cli.Config;

namespace Forge.Cli.Cli;

/// <summary>
/// One `dotnet ef` invocation, built but not yet run.
///
/// <paramref name="Arguments"/> includes the leading "ef" and is the SINGLE source of truth for
/// both display and execution. An earlier version prepended "ef" only in Display, so --dry-run
/// printed a correct command while the process was launched without it — a dry run that lies is
/// worse than no dry run at all.
/// </summary>
public sealed record EfCommand(IReadOnlyList<string> Arguments)
{
    public string Display => "dotnet " + string.Join(" ", Arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;
}

/// <summary>
/// Builds and runs `dotnet ef` commands with --project / --startup-project / --context resolved
/// from forge.config.json.
///
/// Hiding those three flags IS the value of the db:* namespace — getting them wrong is the most
/// common reason `dotnet ef` fails in a layered solution, because the DbContext lives in
/// Infrastructure while the host that configures it lives in the API project.
///
/// Argument construction is pure and separately tested; only Run touches a process.
/// </summary>
public static class EfTool
{
    public static EfCommand MigrationsAdd(ForgeConfig config, string name) =>
        new([..Verb("migrations", "add"), name, .. Targets(config),
             "--output-dir", config.InfrastructureMigrationsPath]);

    public static EfCommand DatabaseUpdate(ForgeConfig config, string? targetMigration = null) =>
        new(targetMigration is null
            ? [.. Verb("database", "update"), .. Targets(config)]
            : [.. Verb("database", "update"), targetMigration, .. Targets(config)]);

    public static EfCommand MigrationsList(ForgeConfig config) =>
        new([.. Verb("migrations", "list"), .. Targets(config)]);

    public static EfCommand DatabaseDrop(ForgeConfig config) =>
        new([.. Verb("database", "drop"), "--force", .. Targets(config)]);

    private static string[] Verb(string group, string action) => ["ef", group, action];

    private static string[] Targets(ForgeConfig config) =>
    [
        "--project", config.InfrastructureProject,
        "--startup-project", config.ApiProject,
        // Passing --context explicitly keeps behaviour deterministic in a solution that grows a
        // second DbContext. doctor already verifies the configured name actually exists.
        "--context", config.ResolvedDbContextName
    ];

    /// <summary>
    /// True when the ambient environment looks like Production. Checked before anything
    /// destructive; a warning from a different command is not a guard.
    /// </summary>
    public static bool IsProduction(out string? environment)
    {
        environment =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "ef", "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return false;
            process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs the command, streaming ef's own output straight through to the user.</summary>
    public static int Run(EfCommand command, string workingDirectory, Output output)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                output.Failure("x Could not start 'dotnet'.");
                return ExitCodes.Error;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            foreach (var line in stdout.Split('\n'))
                if (line.Trim().Length > 0) output.Info(line.TrimEnd());

            if (process.ExitCode != 0)
            {
                foreach (var line in stderr.Split('\n'))
                    if (line.Trim().Length > 0) output.Failure(line.TrimEnd());
            }

            return process.ExitCode == 0 ? ExitCodes.Success : ExitCodes.Error;
        }
        catch (Exception ex)
        {
            output.Failure($"x {ex.Message}");
            return ExitCodes.Error;
        }
    }
}
