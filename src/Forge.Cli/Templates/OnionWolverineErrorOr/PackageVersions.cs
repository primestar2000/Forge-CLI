namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// The package versions a generated solution pins, chosen as a coherent set per target framework.
///
/// This exists because pinning them independently produced a scaffold that straddled three
/// dependency generations at once — EF Core 8, Wolverine/Microsoft.Extensions 9, and a net10.0
/// target. It restored, built and ran, so every gate passed; it only failed the moment a user
/// added a package that pulled the current generation in:
///
///   NU1605: Microsoft.EntityFrameworkCore from 10.0.4 to 8.0.11
///           -> Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 -> EntityFrameworkCore >= 10.0.4
///           -> Infrastructure -> EntityFrameworkCore >= 8.0.11
///
/// and, from WolverineFx 3.6.1 capping every Microsoft.Extensions.* package below 10.0.0, an
/// NU1107 that no per-package nudge can resolve. A scaffold is only useful if the graph it hands
/// over can still move, so these versions have to advance together or not at all.
///
/// Each row is internally consistent: the Wolverine version supports the TFM AND does not cap
/// Microsoft.Extensions.* below what the EF Core version on the same row pulls in.
/// </summary>
public sealed record PackageSet(string EfCore, string Wolverine, string SqlitePclRaw);

public static class PackageVersions
{
    /// <summary>
    /// Wolverine's TFM support is the constraint that shapes this table:
    /// 3.6.1 caps Extensions below 10 (unusable), 5.40.1 spans net8.0-net10.0 with a
    /// &lt; 11.0.0 cap, and 6.x dropped net8.0 entirely. EF Core 10 requires net10.0.
    ///
    /// | TFM     | EF Core | Wolverine | why                                           |
    /// |---------|---------|-----------|-----------------------------------------------|
    /// | net8.0  | 8.0.11  | 5.40.1    | newest Wolverine with net8.0 assets; EF 8 to  |
    /// |         |         |           | avoid the Roslyn clash below                  |
    /// | net9.0  | 9.0.19  | 6.29.1    | EF Core 10 requires net10.0                   |
    /// | net10.0 | 10.0.11 | 6.29.1    | current generation throughout                 |
    ///
    /// The net8.0 row is EF Core 8 rather than 9 for a reason that is invisible until you build
    /// it: EF Core 9's Design package depends on Microsoft.CodeAnalysis.Workspaces.MSBuild 4.8.0,
    /// which requires Microsoft.CodeAnalysis.Common at EXACTLY 4.8.0, while Wolverine 5.x still
    /// carries JasperFx.RuntimeCompiler and pulls Roslyn 4.11+. The pair resolves, builds, and
    /// emits eight NU1608 warnings. Wolverine 6 dropped RuntimeCompiler entirely, which is why
    /// the net9.0 and net10.0 rows can take the newer EF Core cleanly.
    ///
    /// Every row is verified warning-free by scaffolding and building it — see the compile gate.
    /// </summary>
    public static PackageSet For(string targetFramework) => targetFramework switch
    {
        "net8.0" => new(EfCore: "8.0.11", Wolverine: "5.40.1", SqlitePclRaw: Sqlite),
        "net9.0" => new(EfCore: "9.0.19", Wolverine: "6.29.1", SqlitePclRaw: Sqlite),

        // net10.0 and anything newer. A future TFM gets the newest known-good set rather than an
        // error: being one generation behind on a framework forge has not seen yet is recoverable,
        // and refusing to scaffold at all is not.
        _ => new(EfCore: "10.0.11", Wolverine: "6.29.1", SqlitePclRaw: Sqlite)
    };

    /// <summary>
    /// Pinned explicitly on every row. EF Core's Sqlite provider pulls SQLitePCLRaw 2.1.x
    /// transitively, and the 2.1.6 that older EF versions selected carries a high-severity
    /// advisory (NU1903 / GHSA-2m69-gcr7-jv3q). Scaffolding a vulnerable dependency into every
    /// new solution is not acceptable, and an explicit reference is the only way to guarantee
    /// the floor regardless of which EF line the row uses.
    /// </summary>
    private const string Sqlite = "3.0.5";
}
