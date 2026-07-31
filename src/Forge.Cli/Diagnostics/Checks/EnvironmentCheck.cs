using System.Diagnostics;

namespace Forge.Cli.Diagnostics.Checks;

/// <summary>
/// Machine-level prerequisites. These are what a brand-new laptop gets wrong, so the messages
/// carry the exact install command rather than a description of the problem.
/// </summary>
public sealed class EnvironmentCheck : IDoctorCheck
{
    public IEnumerable<CheckResult> Run(DoctorContext context)
    {
        var sdk = RunDotnet("--version");
        yield return sdk.Ok
            ? CheckResult.Pass(".NET SDK", sdk.Output.Trim())
            : CheckResult.Fail(".NET SDK", "could not be determined",
                "Install the .NET SDK: https://dotnet.microsoft.com/download");

        // dotnet-ef gates Tier 1 only — a warning, never a failure.
        var ef = RunDotnet("ef --version");
        yield return ef.Ok
            ? CheckResult.Pass("dotnet-ef", ef.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "installed")
            : CheckResult.Warn("dotnet-ef", "not installed - db:migrate, db:migration, db:rollback unavailable (Tier 1)",
                "dotnet tool install --global dotnet-ef");
    }

    internal static (bool Ok, string Output) RunDotnet(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return (false, string.Empty);

            var output = process.StandardOutput.ReadToEnd();

            // Never let a hung child process hang doctor.
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, string.Empty);
            }

            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
