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
    public string ApplicationResourcesPath { get; set; } = "Common/Resources";
    public string ApplicationNamespace { get; set; } = "";

    public string InfrastructureProject { get; set; } = "";
    public string InfrastructureRepoPath { get; set; } = "Persistence/Repository";
    public string InfrastructureUnitOfWorkImplPath { get; set; } = "Persistence/Repository/Common/UnitOfWork.cs";
    public string InfrastructureConfigurationsPath { get; set; } = "Persistence/Configurations";
    public string InfrastructureMigrationsPath { get; set; } = "Persistence/Migrations";
    public string InfrastructureJobsPath { get; set; } = "BackgroundJobs";
    public string InfrastructureNamespace { get; set; } = "";

    public string ApiProject { get; set; } = "";
    public string ApiNamespace { get; set; } = "";

    /// <summary>EF Core DbContext type name. Falls back to "{SolutionName}DbContext" when unset.</summary>
    public string DbContextName { get; set; } = "";

    [JsonIgnore]
    public string ResolvedDbContextName =>
        string.IsNullOrWhiteSpace(DbContextName) ? $"{SolutionName}DbContext" : DbContextName;

    /// <summary>
    /// Entities get private setters, a constructor and an Update method rather than public
    /// setters. On by default: this is an onion/DDD template, and a freely mutable entity
    /// contradicts the architecture it promises.
    ///
    /// Set false for the anemic shape across the whole solution;
    /// <c>make:entity --public-setters</c> overrides it for one entity.
    /// </summary>
    public bool EncapsulateEntities { get; set; } = true;

    /// <summary>
    /// Base class every generated entity derives from, e.g. "BaseEntity". Empty means none.
    ///
    /// forge reads the base type's own properties out of the domain project and omits them from
    /// the generated entity, so a base carrying Id and audit timestamps does not produce
    /// CS0108 "hides inherited member" on every entity. That lookup is syntactic — the base has
    /// to live in the domain project for forge to see it.
    ///
    /// Set by <c>make:solution --with-base-entity</c>, or by hand for a solution that already
    /// has one.
    /// </summary>
    public string EntityBaseClass { get; set; } = "";

    public string RoleEnum { get; set; } = "UserRole";

    /// <summary>"single-array" or "role-and-subrole" — the two shapes seen in the wild.</summary>
    public string RoleGuardStyle { get; set; } = "single-array";

    /// <summary>"wolverine", "hangfire" or "quartz".</summary>
    public string Scheduler { get; set; } = "wolverine";

    public string StubOverridesPath { get; set; } = ".forge/stubs";

    public ResourceNaming ResourceNaming { get; set; } = new();
}

/// <summary>
/// How response types are named. Config-driven for the same reason roleGuardStyle is: the
/// convention varies per team, and forcing one would mean forking the tool to change it.
///
/// Deliberately responses only. In this template the command/query record IS the inbound
/// contract, so a parallel Request type is ceremony unless the HTTP shape must diverge.
/// </summary>
public sealed class ResourceNaming
{
    public string Suffix { get; set; } = "Response";

    /// <summary>Audience segment, e.g. Public/Customer/Admin/Partner. Empty = no audience.</summary>
    public string[] Audiences { get; set; } = ["Public", "Customer", "Admin", "Partner"];

    /// <summary>View segment, e.g. Summary (list) / Detail (single). Empty = the default view.</summary>
    public string[] Views { get; set; } = ["Summary", "Detail"];

    /// <summary>Segments are dropped when empty, so Order + "" + "" -> OrderResponse.</summary>
    public string Pattern { get; set; } = "{Entity}{Audience}{View}{Suffix}";

    public string Compose(string entity, string? audience, string? view) =>
        Pattern
            .Replace("{Entity}", entity, StringComparison.Ordinal)
            .Replace("{Audience}", audience ?? string.Empty, StringComparison.Ordinal)
            .Replace("{View}", view ?? string.Empty, StringComparison.Ordinal)
            .Replace("{Suffix}", Suffix, StringComparison.Ordinal);
}
