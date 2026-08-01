using System.Text.Json.Nodes;

namespace Forge.Runtime;

/// <summary>
/// Handles one <c>--forge:&lt;verb&gt;</c> inside the application's own process.
///
/// This is the seam that keeps Forge.Runtime framework-agnostic. Anything needing Wolverine, EF
/// or MediatR implements this in a satellite package and registers itself in DI; the core package
/// dispatches by verb name and never learns what those frameworks are. Adding a Wolverine
/// reference to Forge.Runtime would put Wolverine into the restore graph of every consuming
/// application, including those that do not use it — the exact class of conflict tier 2 exists
/// to avoid, and ArchitectureTests fails the build if anyone tries.
/// </summary>
public interface IForgeVerbHandler
{
    /// <summary>Verb this handles, without the "--forge:" prefix. E.g. "invoke".</summary>
    string Verb { get; }

    /// <summary>
    /// Do the work and return the payload for the JSON envelope's "data" field.
    /// Throw <see cref="ForgeRuntimeException"/> for expected, actionable conditions.
    /// </summary>
    Task<JsonNode?> HandleAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken);
}
