using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Handlers;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// The single place that reaches into Wolverine's internals, so an upgrade that moves things
/// breaks in one file with a clear message rather than in several with obscure ones.
///
/// Note <c>HandlerGraph</c> is resolved from the container, not from
/// <c>IWolverineRuntime.Options.HandlerGraph</c> — that property is internal in Wolverine 3.x.
/// Verified against 3.6.1: the graph IS registered in DI, which is the supported public path.
/// </summary>
internal static class WolverineAccess
{
    public static HandlerGraph HandlerGraph(IServiceProvider services) =>
        services.GetService<HandlerGraph>()
        ?? throw new ForgeRuntimeException(
            "Wolverine's HandlerGraph is not available from the container." + Environment.NewLine +
            "  -> Is Wolverine configured?  builder.Host.UseWolverine(...);" + Environment.NewLine +
            "  -> If it is, this Wolverine version may have moved the type; " +
            "upgrade Pitechy.Forge.Runtime.Wolverine.");
}
