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
    bool PinForge = false,
    bool WithRuntime = false,
    bool WithBaseEntity = false,
    bool Swagger = true)
{
    /// <summary>Name of the generated base class. Only meaningful when WithBaseEntity is set.</summary>
    public const string BaseEntityName = "BaseEntity";

    public string ResolvedDbContextName =>
        string.IsNullOrWhiteSpace(DbContextName) ? $"{Name}DbContext" : DbContextName!;

    /// <summary>
    /// Default TFM for GENERATED projects: the major version forge is actually running on.
    ///
    /// Deliberately different from the tool's own net8.0 target. Generated apps get RUN — by
    /// `dotnet ef` when it loads the startup project, and by the developer. Pinning them to an
    /// LTS the machine may not have installed produces projects that build but cannot launch:
    /// observed on a machine with ASP.NET Core 6 and 10 but not 8, where every db:* command
    /// failed with "To install missing framework". Because forge rolls forward, the runtime it
    /// is running on is by definition present.
    /// </summary>
    public static string DefaultTargetFramework =>
        Environment.Version.Major >= 8 ? $"net{Environment.Version.Major}.0" : "net8.0";

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
