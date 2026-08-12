using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Cli.Tests;

/// <summary>
/// How failures inside the user's process are reported back across the boundary.
///
/// Both cases here were found by running invoke:run against a real project rather than by
/// reading code — a denial that arrived as forty frames of Wolverine internals, and a payload
/// that bound nothing and reported success.
/// </summary>
[Collection(Infrastructure.EnvironmentCollection.Name)]
public class RuntimeErrorReportingTests
{
    private sealed class ThrowingHandler(Exception exception) : IForgeVerbHandler
    {
        public string Verb => "invoke";

        public Task<JsonNode?> HandleAsync(IServiceProvider services, string[] args, CancellationToken ct) =>
            throw exception;
    }

    private static async Task<string> RunAsync(Exception thrown)
    {
        // UseEnvironment, not the env var: a bare HostBuilder resolves IHostEnvironment to
        // Production, and the guard prefers the host over the variable. Without this the
        // envelope carries the production refusal and every assertion below would be testing
        // the guard rather than the error reporting.
        var host = new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureServices(services =>
                services.AddSingleton<IForgeVerbHandler>(new ThrowingHandler(thrown)))
            .Build();

        var original = Console.Out;
        var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            await host.RunForgeRuntimeAsync(["--forge:invoke"]);
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
            host.Dispose();
        }
    }

    private static string ErrorOf(string envelope)
    {
        var json = Cli.RuntimeBridge.Extract(envelope);
        Assert.NotNull(json);
        return JsonNode.Parse(json!)!["error"]!.GetValue<string>();
    }

    /// <summary>
    /// A denial is the complete, actionable answer on its own. The generated RoleCheckMiddleware
    /// throws UnauthorizedAccessException, and a stack trace in front of the message obscures
    /// rather than adds.
    /// </summary>
    [Fact]
    public async Task An_authorization_denial_travels_as_a_bare_message()
    {
        var error = ErrorOf(await RunAsync(
            new UnauthorizedAccessException("Access denied: role 'Guest' cannot execute CreateGenreCommand.")));

        Assert.Equal("Access denied: role 'Guest' cannot execute CreateGenreCommand.", error);
        Assert.DoesNotContain("   at ", error);
    }

    /// <summary>Wolverine wraps middleware exceptions, so the denial arrives nested.</summary>
    [Fact]
    public async Task A_wrapped_denial_is_unwrapped()
    {
        var error = ErrorOf(await RunAsync(new InvalidOperationException(
            "Wolverine pipeline failure",
            new UnauthorizedAccessException("Access denied: role 'User' cannot execute DeleteGameCommand."))));

        Assert.Equal("Access denied: role 'User' cannot execute DeleteGameCommand.", error);
        Assert.DoesNotContain("   at ", error);
    }

    /// <summary>
    /// The other half of the contract: a genuine bug keeps its trace, because for those the
    /// trace is the useful part. Collapsing everything to a message would be the opposite bug.
    /// </summary>
    [Fact]
    public async Task A_real_bug_keeps_its_stack_trace()
    {
        Exception captured;
        try { throw new NullReferenceException("Object reference not set."); }
        catch (Exception ex) { captured = ex; }

        var error = ErrorOf(await RunAsync(captured));

        Assert.Contains("NullReferenceException", error);
        Assert.Contains("   at ", error);
    }

    /// <summary>
    /// --payload is hand-typed at a shell against a PascalCase C# record. Binding it
    /// case-sensitively against a camelCase policy silently produced a message with every
    /// property null and then reported success — the command "worked" and wrote a row of nulls.
    /// </summary>
    [Fact]
    public void A_PascalCase_payload_binds_to_a_PascalCase_record()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        Assert.Equal("forge-check",
            JsonSerializer.Deserialize<Payload>("""{"Reference":"forge-check"}""", options)!.Reference);

        // camelCase must keep working — it is what --json output round-trips as.
        Assert.Equal("forge-check",
            JsonSerializer.Deserialize<Payload>("""{"reference":"forge-check"}""", options)!.Reference);
    }

    private sealed record Payload(string Reference);
}
