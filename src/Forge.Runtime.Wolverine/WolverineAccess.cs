using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// The single place that reaches into Wolverine's lifecycle and internals, so an upgrade that
/// moves something breaks in one file with a clear message rather than in several with obscure
/// ones.
/// </summary>
internal static class WolverineAccess
{
    /// <summary>
    /// Bootstraps Wolverine — and ONLY Wolverine — then stops it on dispose.
    ///
    /// This is the non-obvious part of the whole tier-2 design. forge's hook deliberately runs
    /// before app.Run() so the web server never binds a port, which means the host is BUILT but
    /// never STARTED. Wolverine discovers handlers during its hosted-service start, so at that
    /// point HandlerGraph.Chains is empty and every message looks unhandled. Verified: 0 chains
    /// after Build(), 1 after starting the runtime.
    ///
    /// Starting the whole host would fix discovery but reintroduce exactly what the early
    /// short-circuit avoids — Kestrel binding a port, and every unrelated background service
    /// running during what should be a read-only inspection. IWolverineRuntime is itself an
    /// IHostedService, so it can be started on its own.
    /// </summary>
    public static async Task<IAsyncDisposable> StartWolverineAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var runtime = services.GetService<IWolverineRuntime>()
            ?? throw new ForgeRuntimeException(
                "Wolverine is not configured in this application." + Environment.NewLine +
                "  -> builder.Host.UseWolverine(...);");

        if (runtime is not IHostedService hosted)
        {
            // Wolverine changed shape; say so rather than silently reporting zero handlers.
            throw new ForgeRuntimeException(
                "This Wolverine version no longer exposes IWolverineRuntime as an IHostedService, " +
                "so forge cannot bootstrap it without starting the whole host." + Environment.NewLine +
                "  -> Upgrade Pitechy.Forge.Runtime.Wolverine.");
        }

        await hosted.StartAsync(cancellationToken);
        return new RuntimeStopper(hosted);
    }

    public static HandlerGraph HandlerGraph(IServiceProvider services) =>
        services.GetService<HandlerGraph>()
        ?? throw new ForgeRuntimeException(
            "Wolverine's HandlerGraph is not available from the container." + Environment.NewLine +
            "  -> Is Wolverine configured?  builder.Host.UseWolverine(...);" + Environment.NewLine +
            "  -> If it is, this Wolverine version may have moved the type; " +
            "upgrade Pitechy.Forge.Runtime.Wolverine.");

    private sealed class RuntimeStopper(IHostedService hosted) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Best effort: the verb's result already exists by now, and a shutdown failure must
            // not replace a successful invocation with a confusing error.
            try { await hosted.StopAsync(CancellationToken.None); }
            catch { /* ignored */ }
        }
    }
}
