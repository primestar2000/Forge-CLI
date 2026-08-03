namespace Forge.Cli.Tests.Infrastructure;

/// <summary>
/// Serialises every test that mutates a process-global environment variable.
///
/// xUnit runs test classes in parallel by default, and ASPNETCORE_ENVIRONMENT is process-wide —
/// so two classes setting it race, and the loser reads the other's value. This surfaced as
/// The_runtime_refuses_to_run_in_production passing alone and failing in the full suite, which
/// is the worst kind of failure: it looks like flakiness rather than a real defect.
///
/// Apply [Collection(EnvironmentCollection.Name)] to any class that touches one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "environment-variables";
}
