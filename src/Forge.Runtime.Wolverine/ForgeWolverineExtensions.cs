using Microsoft.Extensions.DependencyInjection;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// Registers Wolverine support for forge. One line alongside the runtime hook:
///
/// <code>
/// builder.Services.AddForgeWolverine();
/// ...
/// var app = builder.Build();
/// if (await app.RunForgeRuntimeAsync(args)) return;
/// </code>
///
/// Registering the handlers is all this does — it adds no behaviour to a normal application
/// start, because <c>RunForgeRuntimeAsync</c> only dispatches when forge launched the process.
/// </summary>
public static class ForgeWolverineExtensions
{
    public static IServiceCollection AddForgeWolverine(this IServiceCollection services)
    {
        services.AddSingleton<IForgeVerbHandler, HandlersVerbHandler>();
        services.AddSingleton<IForgeVerbHandler, InvokeVerbHandler>();
        return services;
    }

    /// <summary>
    /// Registers a message factory so <c>forge invoke:run</c> can build a payload without you
    /// typing JSON. Sugar for the two registrations a factory needs.
    /// </summary>
    public static IServiceCollection AddForgeMessageFactory<TFactory, TMessage>(this IServiceCollection services)
        where TFactory : class, IForgeMessageFactory<TMessage>
    {
        services.AddSingleton<TFactory>();
        services.AddSingleton<IForgeMessageFactory<TMessage>>(sp => sp.GetRequiredService<TFactory>());
        return services;
    }
}
