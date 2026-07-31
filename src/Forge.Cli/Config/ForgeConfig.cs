using System.Text.Json.Serialization;

namespace Forge.Cli.Config;

/// <summary>
/// Deserialized forge.config.json. Every generator reads paths from here — a hardcoded "src/"
/// or "Entities" anywhere in the codebase is a bug, because it breaks every team whose layout
/// differs, which is the entire reason this file exists.
/// </summary>
public sealed class ForgeConfig
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Lets a future forge migrate or refuse an old config instead of misreading it.</summary>
    public int Version { get; set; } = CurrentVersion;

    public string Template { get; set; } = "onion-wolverine-erroror";
    public string SolutionName { get; set; } = "";

    public string DomainProject { get; set; } = "";
    public string DomainEntitiesPath { get; set; } = "Entities";
    public string DomainNamespace { get; set; } = "";

    public string ApplicationProject { get; set; } = "";
    public string ApplicationFeaturesPath { get; set; } = "Features";
    public string ApplicationRepoPath { get; set; } = "Common/Interfaces/Persistence";
    public string ApplicationUnitOfWorkInterfacePath { get; set; } = "Common/Interfaces/Persistence/Common/IUnitOfWork.cs";
    public string ApplicationErrorsPath { get; set; } = "Common/Errors";
    public string ApplicationNamespace { get; set; } = "";

    public string InfrastructureProject { get; set; } = "";
    public string InfrastructureRepoPath { get; set; } = "Persistence/Repository";
    public string InfrastructureUnitOfWorkImplPath { get; set; } = "Persistence/Repository/Common/UnitOfWork.cs";
    public string InfrastructureConfigurationsPath { get; set; } = "Persistence/Configurations";
    public string InfrastructureJobsPath { get; set; } = "BackgroundJobs";
    public string InfrastructureNamespace { get; set; } = "";

    public string ApiProject { get; set; } = "";
    public string ApiNamespace { get; set; } = "";

    /// <summary>EF Core DbContext type name. Falls back to "{SolutionName}DbContext" when unset.</summary>
    public string DbContextName { get; set; } = "";

    [JsonIgnore]
    public string ResolvedDbContextName =>
        string.IsNullOrWhiteSpace(DbContextName) ? $"{SolutionName}DbContext" : DbContextName;

    public string RoleEnum { get; set; } = "UserRole";

    /// <summary>"single-array" or "role-and-subrole" — the two shapes seen in the wild.</summary>
    public string RoleGuardStyle { get; set; } = "single-array";

    /// <summary>"wolverine", "hangfire" or "quartz".</summary>
    public string Scheduler { get; set; } = "wolverine";

    public string StubOverridesPath { get; set; } = ".forge/stubs";
}
