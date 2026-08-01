using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// Backs <c>forge invoke:run</c> — drives one message through the REAL Wolverine pipeline, so
/// middleware (role checks, validation, logging) runs exactly as it does in production.
///
/// Uses InvokeAsync rather than Send/Publish because it is the only one that runs the pipeline
/// synchronously in-process AND returns the handler's result, which is the whole point: the
/// caller wants to see what came back.
/// </summary>
public sealed class InvokeVerbHandler : IForgeVerbHandler
{
    public string Verb => "invoke";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<JsonNode?> HandleAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var graph = WolverineAccess.HandlerGraph(services);

        var requested = ForgeRuntimeArgs.Option(args, "message")
            ?? throw new ForgeRuntimeException("No message type was specified.");

        var messageType = ResolveMessageType(graph, requested);
        var message = BuildMessage(services, messageType, args);

        var bus = services.GetRequiredService<IMessageBus>();

        var started = DateTime.UtcNow;
        var result = await bus.InvokeAsync<object>(message, cancellationToken);
        var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;

        return new JsonObject
        {
            ["messageType"] = messageType.Name,
            ["payload"] = Serialise(message),
            ["result"] = Serialise(result),
            ["resultType"] = result?.GetType().Name,
            ["isError"] = TryReadIsError(result),
            ["milliseconds"] = elapsed
        };
    }

    /// <summary>
    /// Matches on simple name first, then full name. Listing the known types on a miss matters:
    /// the usual cause is a typo or a message with no registered handler, and both are answered
    /// by seeing the real list.
    /// </summary>
    private static Type ResolveMessageType(HandlerGraph graph, string requested)
    {
        var known = graph.Chains.Select(c => c.MessageType).Distinct().ToList();

        var matches = known
            .Where(t => string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t.FullName, requested, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0];

        if (matches.Count > 1)
        {
            throw new ForgeRuntimeException(
                $"'{requested}' is ambiguous - {matches.Count} message types share that name." + Environment.NewLine +
                $"  -> Use the full name: {string.Join(", ", matches.Select(m => m.FullName))}");
        }

        throw new ForgeRuntimeException(
            $"No Wolverine handler is registered for a message type named '{requested}'." + Environment.NewLine +
            (known.Count == 0
                ? "  -> This application has no Wolverine handlers at all."
                : $"  -> Known: {string.Join(", ", known.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal))}"));
    }

    /// <summary>
    /// Payload resolution order: explicit JSON wins, then a named factory, then a sole registered
    /// factory. Explicit input always beats inference — otherwise a factory could silently
    /// override what the caller actually asked for.
    /// </summary>
    private static object BuildMessage(IServiceProvider services, Type messageType, string[] args)
    {
        var payload = ForgeRuntimeArgs.Option(args, "payload");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                return JsonSerializer.Deserialize(payload!, messageType, SerializerOptions)
                       ?? throw new ForgeRuntimeException($"--payload deserialised to null for {messageType.Name}.");
            }
            catch (JsonException ex)
            {
                throw new ForgeRuntimeException(
                    $"--payload is not valid JSON for {messageType.Name}: {ex.Message}");
            }
        }

        var state = ForgeRuntimeArgs.Option(args, "state");
        var requestedFactory = ForgeRuntimeArgs.Option(args, "factory");

        var factories = FindFactories(services, messageType);

        if (requestedFactory is not null)
        {
            var factory = factories.FirstOrDefault(f =>
                string.Equals(f.GetType().Name, requestedFactory, StringComparison.OrdinalIgnoreCase))
                ?? throw new ForgeRuntimeException(
                    $"No IForgeMessageFactory<{messageType.Name}> named '{requestedFactory}' is registered." +
                    Environment.NewLine +
                    (factories.Count == 0
                        ? "  -> None are registered for this message type."
                        : $"  -> Registered: {string.Join(", ", factories.Select(f => f.GetType().Name))}"));

            return Create(factory, messageType, state);
        }

        if (factories.Count == 1) return Create(factories[0], messageType, state);

        if (factories.Count > 1)
        {
            throw new ForgeRuntimeException(
                $"{factories.Count} factories are registered for {messageType.Name}." + Environment.NewLine +
                $"  -> Choose one with --factory: {string.Join(", ", factories.Select(f => f.GetType().Name))}");
        }

        throw new ForgeRuntimeException(
            $"No payload available for {messageType.Name}." + Environment.NewLine +
            "  -> Pass --payload '<json>', or register an IForgeMessageFactory<" + messageType.Name + ">." +
            Environment.NewLine +
            "  -> forge make:factory can scaffold one.");
    }

    private static List<object> FindFactories(IServiceProvider services, Type messageType)
    {
        var factoryType = typeof(IForgeMessageFactory<>).MakeGenericType(messageType);
        var enumerable = typeof(IEnumerable<>).MakeGenericType(factoryType);

        return services.GetService(enumerable) is not System.Collections.IEnumerable resolved
            ? []
            : resolved.Cast<object>().ToList();
    }

    private static object Create(object factory, Type messageType, string? state)
    {
        var method = state is null
            ? factory.GetType().GetMethod("Create", Type.EmptyTypes)
            : factory.GetType().GetMethod("Create", [typeof(string)]);

        if (method is null)
            throw new ForgeRuntimeException($"{factory.GetType().Name} has no usable Create method.");

        try
        {
            return method.Invoke(factory, state is null ? null : [state])
                   ?? throw new ForgeRuntimeException($"{factory.GetType().Name}.Create returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new ForgeRuntimeException(
                $"{factory.GetType().Name}.Create threw: {ex.InnerException.Message}");
        }
    }

    /// <summary>
    /// Serialises defensively. A result may be an ErrorOr&lt;T&gt; or any other discriminated
    /// wrapper whose Value property throws when it holds an error — so a serialisation failure
    /// must degrade to ToString() rather than lose the result entirely.
    /// </summary>
    private static JsonNode? Serialise(object? value)
    {
        if (value is null) return null;

        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["_unserialisable"] = value.GetType().Name,
                ["_reason"] = ex.Message,
                ["value"] = value.ToString()
            };
        }
    }

    /// <summary>
    /// Reads an IsError flag if the result exposes one, without this package taking a dependency
    /// on ErrorOr — which is template-specific and must not leak into a Wolverine-generic package.
    /// </summary>
    private static bool? TryReadIsError(object? result)
    {
        if (result is null) return null;

        var property = result.GetType().GetProperty("IsError", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool)) return null;

        try { return (bool?)property.GetValue(result); }
        catch { return null; }
    }
}
