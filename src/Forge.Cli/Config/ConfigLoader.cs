using System.Text.Json;
using Forge.Cli.Cli;

namespace Forge.Cli.Config;

public sealed record ConfigLoadResult(
    ForgeConfig? Config,
    string? SolutionRoot,
    int ExitCode,
    string? Error)
{
    public bool Ok => ExitCode == ExitCodes.Success;
}

public static class ConfigLoader
{
    public const string FileName = "forge.config.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> looking for forge.config.json, then
    /// for a .sln as a fallback marker — the same way the dotnet CLI finds a solution.
    /// </summary>
    public static ConfigLoadResult Load(string startDirectory)
    {
        var configPath = Fs.FindFileUpward(startDirectory, FileName);
        if (configPath is null)
        {
            var sln = Fs.FindByPatternUpward(startDirectory, "*.sln");
            var hint = sln is not null
                ? $"Found a solution at {sln} but no {FileName} beside it."
                : $"No {FileName} found in {startDirectory} or any parent directory.";

            return new ConfigLoadResult(null, null, ExitCodes.ConfigInvalid,
                $"{hint}{Environment.NewLine}" +
                $"  -> Run 'forge init' to generate one from an existing solution,{Environment.NewLine}" +
                $"     or 'forge make:solution -n <Name>' to scaffold a new one.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

        ForgeConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ForgeConfig>(File.ReadAllText(configPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ConfigLoadResult(null, root, ExitCodes.ConfigInvalid,
                $"{configPath} is not valid JSON: {ex.Message}");
        }

        if (config is null)
        {
            return new ConfigLoadResult(null, root, ExitCodes.ConfigInvalid, $"{configPath} is empty.");
        }

        if (config.Version > ForgeConfig.CurrentVersion)
        {
            return new ConfigLoadResult(null, root, ExitCodes.ConfigInvalid,
                $"{configPath} declares version {config.Version}, but this forge understands up to " +
                $"{ForgeConfig.CurrentVersion}.{Environment.NewLine}  -> Upgrade forge: dotnet tool update --local Pitechy.Forge.Cli");
        }

        return new ConfigLoadResult(config, root, ExitCodes.Success, null);
    }

    /// <summary>
    /// Checks that every configured path actually resolves. Fast enough for a pre-commit hook,
    /// and it catches a stale config before a generator fails halfway through a multi-file write.
    /// </summary>
    public static IReadOnlyList<string> Validate(ForgeConfig config, string solutionRoot)
    {
        var problems = new List<string>();

        void MustExist(string relative, string label)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                problems.Add($"{label} is not set in {FileName}");
                return;
            }
            var full = Path.Combine(solutionRoot, relative);
            if (!Directory.Exists(full) && !File.Exists(full))
                problems.Add($"{label} -> '{relative}' does not exist (looked in {full})");
        }

        MustExist(config.DomainProject, "domainProject");
        MustExist(config.ApplicationProject, "applicationProject");
        MustExist(config.InfrastructureProject, "infrastructureProject");
        MustExist(config.ApiProject, "apiProject");

        // The files generators PATCH must exist — folders they merely write into are created on
        // demand, but a wrong UnitOfWork path fails a command halfway through, which is exactly
        // what config:validate exists to catch first.
        void MustBeFile(string projectRelative, string fileRelative, string label)
        {
            if (string.IsNullOrWhiteSpace(projectRelative) || string.IsNullOrWhiteSpace(fileRelative))
            {
                problems.Add($"{label} is not set in {FileName}");
                return;
            }
            var full = Path.Combine(solutionRoot, projectRelative, fileRelative);
            if (!File.Exists(full))
                problems.Add($"{label} -> '{fileRelative}' does not exist (looked in {full})");
        }

        MustBeFile(config.ApplicationProject, config.ApplicationUnitOfWorkInterfacePath,
            "applicationUnitOfWorkInterfacePath");
        MustBeFile(config.InfrastructureProject, config.InfrastructureUnitOfWorkImplPath,
            "infrastructureUnitOfWorkImplPath");

        if (config.RoleGuardStyle is not ("single-array" or "role-and-subrole"))
            problems.Add($"roleGuardStyle '{config.RoleGuardStyle}' is not one of: single-array, role-and-subrole");

        if (config.Scheduler is not ("wolverine" or "hangfire" or "quartz"))
            problems.Add($"scheduler '{config.Scheduler}' is not one of: wolverine, hangfire, quartz");

        return problems;
    }

    // Upward searching lives in Fs, which bounds the walk at the repository boundary and the
    // user profile directory. An unbounded walk escapes the project — see Fs.FindUpward.
}
