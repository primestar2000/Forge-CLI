namespace Forge.Runtime;

/// <summary>
/// A database seeder, run by <c>forge db:seed</c> inside your application's own process and DI
/// container — so it gets the real DbContext, the real connection string, and real configuration.
///
/// Register your seeders normally; forge resolves <c>IEnumerable&lt;ISeeder&gt;</c>:
/// <code>
/// services.AddScoped&lt;ISeeder, RoleSeeder&gt;();
/// services.AddScoped&lt;ISeeder, UserSeeder&gt;();
/// </code>
///
/// <b>Seeders must be idempotent.</b> They are re-run against databases that already contain
/// rows, so prefer upsert-style logic over inserts that throw on the second run.
/// </summary>
public interface ISeeder
{
    /// <summary>
    /// Lower runs first. Use it when one seeder depends on another's rows — e.g. roles before
    /// users. Ties are broken by type name so ordering is stable across runs.
    /// </summary>
    int Order => 0;

    /// <summary>Display name in forge's output. Defaults to the implementing type's name.</summary>
    string Name => GetType().Name;

    /// <summary>
    /// Seed. Return the number of records created or updated, or null if not meaningful —
    /// forge reports it so a run tells you whether anything actually changed.
    /// </summary>
    Task<int?> SeedAsync(CancellationToken cancellationToken = default);
}
