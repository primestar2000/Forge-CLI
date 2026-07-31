using System.Security.Cryptography;
using System.Text;

namespace Forge.Cli.Templates;

public sealed record SolutionSpec(
    string Name,
    string Directory,
    string RoleGuardStyle,
    string Scheduler,
    string TargetFramework,
    string RoleEnum,
    string? DbContextName,
    bool PinForge = false)
{
    public string ResolvedDbContextName =>
        string.IsNullOrWhiteSpace(DbContextName) ? $"{Name}DbContext" : DbContextName!;

    /// <summary>
    /// Deterministic project GUID derived from the project name.
    ///
    /// Deliberately NOT Guid.NewGuid(): re-running make:solution must produce byte-identical
    /// output, otherwise the command is not idempotent and --dry-run could not be trusted.
    /// </summary>
    public static string ProjectGuid(string projectName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("forge:" + projectName));
        return "{" + new Guid(hash).ToString().ToUpperInvariant() + "}";
    }
}
