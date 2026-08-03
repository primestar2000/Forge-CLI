using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Handlers;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// Backs <c>forge invoke:list</c> — every message type Wolverine has a handler for, read from the
/// real runtime rather than guessed at by scanning source.
/// </summary>
public sealed class HandlersVerbHandler : IForgeVerbHandler
{
    public string Verb => "handlers";

    public async Task<JsonNode?> HandleAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        // Wolverine discovers handlers on start, and forge's hook runs before the host starts.
        await using var _ = await WolverineAccess.StartWolverineAsync(services, cancellationToken);

        var graph = WolverineAccess.HandlerGraph(services);

        var messages = new JsonArray();

        foreach (var chain in graph.Chains.OrderBy(c => c.MessageType.FullName, StringComparer.Ordinal))
        {
            messages.Add(new JsonObject
            {
                ["messageType"] = chain.MessageType.Name,
                ["fullName"] = chain.MessageType.FullName,
                ["namespace"] = chain.MessageType.Namespace,
                ["assembly"] = chain.MessageType.Assembly.GetName().Name,
                // Wolverine allows several handlers per message; listing them makes "why did two
                // things happen" answerable without reading the codebase.
                ["handlers"] = new JsonArray(chain.HandlerCalls()
                    .Select(call => (JsonNode?)JsonValue.Create(call.HandlerType.Name))
                    .ToArray())
            });
        }

        return new JsonObject
        {
            ["messages"] = messages,
            ["count"] = messages.Count
        };
    }
}
