using System.Text.Json.Nodes;
using Forge.Cli.Cli;
using Forge.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Cli.Tests;

/// <summary>
/// Identity resolution for invoke:run, and the production guards.
///
/// Context for why identity exists at all: the generated pipeline is secure by default, so a
/// CLI-launched process has nobody signed in and every guarded message is denied as Guest.
/// Verified end to end — CreateGigCommand is denied without an identity and succeeds with one.
/// </summary>
[Collection(Infrastructure.EnvironmentCollection.Name)]
public class IdentityResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-id-" + Guid.NewGuid().ToString("N")[..8]);

    public IdentityResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void WriteIdentity(string name, string json)
    {
        var directory = Path.Combine(_root, ".forge", "identities");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    [Fact]
    public void No_flags_yields_no_identity()
    {
        var result = IdentityResolver.Resolve(_root, null, null);

        Assert.True(result.Ok);
        Assert.Null(result.Json);
    }

    [Fact]
    public void As_role_synthesises_a_minimal_identity()
    {
        var json = JsonNode.Parse(IdentityResolver.Resolve(_root, null, "Admin").Json!)!.AsObject();

        Assert.Equal("Admin", json["role"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["email"]!.GetValue<string>()));
    }

    [Fact]
    public void As_loads_a_named_identity_file()
    {
        WriteIdentity("admin", """{"id":"8f2c9a11-4d3e-4a7b-9f10-2c5e7b1d4a90","email":"admin@shop.test","role":"Admin"}""");

        var json = JsonNode.Parse(IdentityResolver.Resolve(_root, "admin", null).Json!)!.AsObject();

        Assert.Equal("Admin", json["role"]!.GetValue<string>());
        Assert.Equal("admin@shop.test", json["email"]!.GetValue<string>());
    }

    /// <summary>--as is the more specific request; preferring the sugar would discard it.</summary>
    [Fact]
    public void As_wins_over_as_role()
    {
        WriteIdentity("admin", """{"email":"admin@shop.test","role":"Admin"}""");

        var json = JsonNode.Parse(IdentityResolver.Resolve(_root, "admin", "User").Json!)!.AsObject();

        Assert.Equal("Admin", json["role"]!.GetValue<string>());
    }

    [Fact]
    public void An_unknown_name_lists_what_is_available()
    {
        WriteIdentity("admin", """{"role":"Admin"}""");
        WriteIdentity("customer", """{"role":"User"}""");

        var result = IdentityResolver.Resolve(_root, "nope", null);

        Assert.False(result.Ok);
        Assert.Contains("admin", result.Error);
        Assert.Contains("customer", result.Error);
    }

    /// <summary>Validated CLI-side so a typo fails in milliseconds, not after a build and host start.</summary>
    [Fact]
    public void Malformed_json_fails_before_launching_anything()
    {
        WriteIdentity("broken", "{ this is not json");

        var result = IdentityResolver.Resolve(_root, "broken", null);

        Assert.False(result.Ok);
        Assert.Contains("not valid JSON", result.Error);
    }

    [Fact]
    public void An_identity_without_a_role_is_rejected()
    {
        WriteIdentity("roleless", """{"email":"x@y.z"}""");

        var result = IdentityResolver.Resolve(_root, "roleless", null);

        Assert.False(result.Ok);
        Assert.Contains("role", result.Error);
    }

    // ---- production guards ----------------------------------------------------------------

    private static IHost EmptyHost() => new HostBuilder().Build();

    private static async Task<(bool Handled, string Output)> CaptureAsync(Func<Task<bool>> action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            return (await action(), writer.ToString());
        }
        finally { Console.SetOut(original); }
    }

    /// <summary>
    /// The hole this closes: anything able to pass --forge:seed to a production process would
    /// otherwise get the seeders executed. A misconfigured container CMD is a likelier route
    /// than an attacker, and the blast radius is the production database either way.
    /// </summary>
    [Fact]
    public async Task The_runtime_refuses_to_run_in_production()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            using var host = EmptyHost();

            var result = await CaptureAsync(() => host.RunForgeRuntimeAsync(["--forge:seed"]));

            // Handled, but refused — the caller must not fall through to app.Run().
            Assert.True(result.Handled);
            Assert.Contains("\"success\":false", result.Output);
            Assert.Contains("Production", result.Output);
        }
        finally { Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous); }
    }

    [Fact]
    public async Task The_enabled_switch_disables_the_hook_outright()
    {
        using var host = EmptyHost();

        var result = await CaptureAsync(() => host.RunForgeRuntimeAsync(["--forge:seed"], enabled: false));

        Assert.False(result.Handled);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void IsForgeInvocation_detects_the_sentinel_and_nothing_else()
    {
        Assert.True(ForgeRuntimeExtensions.IsForgeInvocation(["--forge:seed"]));
        Assert.True(ForgeRuntimeExtensions.IsForgeInvocation(["--urls", "http://x", "--forge:invoke"]));

        // The registration swap in generated Program.cs keys off this: a false positive would
        // silently change CurrentUser's lifetime for a real web request.
        Assert.False(ForgeRuntimeExtensions.IsForgeInvocation([]));
        Assert.False(ForgeRuntimeExtensions.IsForgeInvocation(["--urls", "http://localhost:5000"]));
        Assert.False(ForgeRuntimeExtensions.IsForgeInvocation(["--environment", "Production"]));
    }
}
