using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Runtime;

/// <summary>
/// The hook forge calls into. One line in Program.cs:
///
/// <code>
/// var app = builder.Build();
/// if (await app.RunForgeRuntimeAsync(args)) return;   // no-op unless forge invoked this
/// app.Run();
/// </code>
///
/// It short-circuits BEFORE app.Run(), so the web server never binds a port — running
/// <c>forge db:seed</c> while the app is already running in another terminal cannot collide.
/// The host is fully built by this point, so seeders get the real container, the real
/// DbContext and the real configuration.
/// </summary>
public static class ForgeRuntimeExtensions
{
    /// <summary>Sentinel prefix. Chosen to be something no real application would define.</summary>
    public const string ArgumentPrefix = "--forge:";

    /// <summary>
    /// Runs a forge verb if this process was launched by forge, and reports whether it did.
    ///
    /// Returns false — doing nothing at all — for a normal application start, which is the case
    /// that matters: this must be invisible in production.
    /// </summary>
    /// <summary>
    /// True when this process was launched by forge. Exposed so the application can switch
    /// registrations for the invocation — see the generated Program.cs, which registers the
    /// current-user services as singletons in forge mode because the process handles exactly one
    /// message. Cheap and side-effect free; safe to call before the container is built.
    /// </summary>
    public static bool IsForgeInvocation(string[] args) => FindVerb(args) is not null;

    /// <param name="enabled">
    /// Explicit kill switch. Pass <c>builder.Environment.IsDevelopment()</c> to make the
    /// intent visible at the call site. When false this returns immediately, whatever the
    /// arguments say.
    /// </param>
    public static async Task<bool> RunForgeRuntimeAsync(
        this IHost host,
        string[] args,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var verb = FindVerb(args);
        if (verb is null) return false;

        if (enabled == false) return false;

        // Defence in depth: without this, anything able to pass --forge:seed to a production
        // process gets the seeders executed. A misconfigured container CMD is a likelier route
        // than an attacker, and either way the blast radius is the production database.
        if (IsProduction(host) && !ForgeRuntimeArgs.Flag(args, "allow-production"))
        {
            Console.Out.Write(ForgeEnvelope.Failure(verb,
                "Refusing to run: the hosting environment is Production." + Environment.NewLine +
                "  -> forge is a development tool and will not touch a production process." + Environment.NewLine +
                "  -> Note .NET treats an UNSET environment as Production, so if this is a dev " +
                "machine, set ASPNETCORE_ENVIRONMENT=Development." + Environment.NewLine +
                "  -> If this is genuinely intended, re-run with --i-know-this-is-production."));

            await Console.Out.FlushAsync();
            return true;
        }

        try
        {
            var data = await DispatchAsync(host, verb, args, cancellationToken);

            Console.Out.Write(ForgeEnvelope.Success(verb, data));
        }
        catch (Exception ex)
        {
            // The exception must reach forge as data, not as an unhandled crash: forge needs to
            // render it rather than the user decoding a stack trace from a child process.
            // Expected conditions travel as a bare message; real bugs keep their stack trace,
            // because for those the trace is the useful part.
            Console.Out.Write(ForgeEnvelope.Failure(verb, Describe(ex)));
        }

        await Console.Out.FlushAsync();
        return true;
    }

    /// <summary>
    /// Turns an exception into what forge shows the user.
    ///
    /// A stack trace is the right answer for a bug and the wrong one for a denial. Being told
    /// "role 'Guest' cannot execute CreateGenreCommand" is the complete, actionable answer;
    /// forty frames of Wolverine internals in front of it actively obscure that. An
    /// authorization failure is an expected outcome of invoke:run against a guarded message,
    /// so it travels as a bare message like ForgeRuntimeException does.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        ForgeRuntimeException => ex.Message,
        UnauthorizedAccessException => ex.Message,

        // Wolverine wraps handler and middleware exceptions, so the denial arrives nested.
        { InnerException: UnauthorizedAccessException inner } => inner.Message,

        _ => ex.ToString()
    };

    /// <summary>
    /// Registered handlers win over the built-ins, so a satellite package can extend or replace
    /// a verb without the core package knowing it exists.
    /// </summary>
    private static async Task<JsonNode?> DispatchAsync(
        IHost host,
        string verb,
        string[] args,
        CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();

        var handler = scope.ServiceProvider
            .GetServices<IForgeVerbHandler>()
            .LastOrDefault(h => string.Equals(h.Verb, verb, StringComparison.OrdinalIgnoreCase));

        if (handler is not null)
            return await handler.HandleAsync(scope.ServiceProvider, args, cancellationToken);

        if (string.Equals(verb, "seed", StringComparison.OrdinalIgnoreCase))
            return await RunSeedersAsync(scope.ServiceProvider, args, cancellationToken);

        // Naming the satellite package matters: "unsupported verb" alone leaves the user guessing
        // whether they need an upgrade or a different package entirely.
        var hint = verb.Equals("invoke", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("handlers", StringComparison.OrdinalIgnoreCase)
            ? "  -> invoke:* needs the Wolverine satellite package:" + Environment.NewLine +
              "       dotnet add package Pitechy.Forge.Runtime.Wolverine" + Environment.NewLine +
              "       builder.Services.AddForgeWolverine();"
            : "  -> Upgrade the package: dotnet add package Pitechy.Forge.Runtime";

        throw new ForgeRuntimeException(
            $"Pitechy.Forge.Runtime {typeof(ForgeRuntimeExtensions).Assembly.GetName().Version?.ToString(3)} " +
            $"has no handler for the verb '{verb}'." + Environment.NewLine + hint);
    }

    /// <summary>
    /// Reads the environment from IHostEnvironment when available, falling back to the ambient
    /// variables. The fallback matters: a plain Host (no web builder) may not register
    /// IHostEnvironment the same way, and a guard that silently fails open is not a guard.
    /// </summary>
    private static bool IsProduction(IHost host)
    {
        var fromHost = host.Services.GetService<IHostEnvironment>()?.EnvironmentName;

        var environment = fromHost
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindVerb(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
            {
                var verb = argument[ArgumentPrefix.Length..].Trim();
                if (verb.Length > 0) return verb;
            }
        }

        return null;
    }

    internal static string? FindOption(string[] args, string name) => ForgeRuntimeArgs.Option(args, name);

    private static async Task<JsonNode> RunSeedersAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var only = FindOption(args, "only");

        var seeders = services
            .GetServices<ISeeder>()
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        if (only is not null)
        {
            seeders = seeders
                .Where(s => s.Name.Equals(only, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (seeders.Count == 0)
            {
                var registered = services.GetServices<ISeeder>().Select(s => s.Name).ToList();
                throw new ForgeRuntimeException(
                    $"No registered ISeeder named '{only}'." + Environment.NewLine +
                    (registered.Count == 0
                        ? "  -> No seeders are registered at all: services.AddScoped<ISeeder, MySeeder>();"
                        : $"  -> Registered: {string.Join(", ", registered)}"));
            }
        }

        var results = new JsonArray();

        foreach (var seeder in seeders)
        {
            var started = DateTime.UtcNow;
            var affected = await seeder.SeedAsync(cancellationToken);

            results.Add(new JsonObject
            {
                ["name"] = seeder.Name,
                ["order"] = seeder.Order,
                ["affected"] = affected,
                ["milliseconds"] = (int)(DateTime.UtcNow - started).TotalMilliseconds
            });
        }

        return new JsonObject
        {
            ["seeders"] = results,
            ["count"] = results.Count
        };
    }
}
