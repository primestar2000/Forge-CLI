using Forge.Cli.Cli;
using Forge.Cli.Config;
using Forge.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Cli.Tests;

/// <summary>
/// The CLI/runtime boundary. Both halves are exercised in-process here; the actual
/// launch-the-user's-app path is covered end to end by the compile gate.
/// </summary>
public class RuntimeBridgeTests
{
    // ---- envelope extraction ------------------------------------------------------------

    /// <summary>
    /// The payload shares stdout with the application's own logging — startup banners, EF SQL,
    /// Serilog. Extraction must find it in the noise rather than parsing the whole stream.
    /// </summary>
    [Fact]
    public void Extracts_the_payload_from_a_stream_full_of_application_logging()
    {
        var output = string.Join('\n',
            "info: Microsoft.Hosting.Lifetime[0] Application starting",
            "warn: Some.Component[3] a warning with <<< angle brackets >>>",
            ForgeEnvelope.BeginMarker,
            """{"schemaVersion":1,"success":true}""",
            ForgeEnvelope.EndMarker,
            "info: Microsoft.Hosting.Lifetime[0] Application shutting down");

        Assert.Equal("""{"schemaVersion":1,"success":true}""", RuntimeBridge.Extract(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("info: nothing to see here")]
    [InlineData("<<<FORGE-RESULT>>> unterminated")]
    public void Returns_null_when_no_complete_envelope_is_present(string output) =>
        Assert.Null(RuntimeBridge.Extract(output));

    /// <summary>
    /// The CLI and the runtime each parse this independently — they share a schema, never a type,
    /// which is the whole point of the process boundary. Pin that they agree.
    /// </summary>
    [Fact]
    public void Cli_and_runtime_agree_on_the_envelope_format()
    {
        var envelope = ForgeEnvelope.Success("seed", null);

        Assert.Equal(ForgeEnvelope.Extract(envelope), RuntimeBridge.Extract(envelope));
        Assert.NotNull(RuntimeBridge.Extract(envelope));
    }

    // ---- sentinel parsing ---------------------------------------------------------------

    [Fact]
    public void Runtime_hook_is_inert_for_a_normal_application_start()
    {
        // The overwhelmingly common case: this must do nothing at all in production.
        Assert.Null(ForgeRuntimeExtensions.FindVerb([]));
        Assert.Null(ForgeRuntimeExtensions.FindVerb(["--urls", "http://localhost:5000"]));
        Assert.Null(ForgeRuntimeExtensions.FindVerb(["--environment", "Production"]));
    }

    [Fact]
    public void Runtime_hook_recognises_the_sentinel_anywhere_in_argv()
    {
        Assert.Equal("seed", ForgeRuntimeExtensions.FindVerb(["--forge:seed"]));
        Assert.Equal("seed", ForgeRuntimeExtensions.FindVerb(["--urls", "http://x", "--forge:seed"]));
        Assert.Equal("invoke", ForgeRuntimeExtensions.FindVerb(["--forge:invoke", "--forge-message", "X"]));
    }

    [Fact]
    public void The_cli_builds_the_argument_the_runtime_looks_for()
    {
        // Regression guard for the class of bug where display and execution disagreed.
        var argument = RuntimeBridge.ForgeRuntimeArgument("seed");

        Assert.Equal("--forge:seed", argument);
        Assert.Equal("seed", ForgeRuntimeExtensions.FindVerb([argument]));
    }

    [Fact]
    public void Runtime_options_are_read_by_name()
    {
        Assert.Equal("UserSeeder", ForgeRuntimeExtensions.FindOption(["--forge:seed", "--forge-only", "UserSeeder"], "only"));
        Assert.Null(ForgeRuntimeExtensions.FindOption(["--forge:seed"], "only"));
        // A trailing flag with no value must not read past the end of the array.
        Assert.Null(ForgeRuntimeExtensions.FindOption(["--forge:seed", "--forge-only"], "only"));
    }

    // ---- tier-2 detection ---------------------------------------------------------------

    [Fact]
    public void Detects_whether_the_runtime_package_is_referenced()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-rt-" + Guid.NewGuid().ToString("N")[..8]);
        var api = Path.Combine(root, "src", "App.API");
        Directory.CreateDirectory(api);

        var config = new ForgeConfig { ApiProject = "src/App.API" };

        try
        {
            File.WriteAllText(Path.Combine(api, "App.API.csproj"), "<Project></Project>");
            Assert.False(RuntimeBridge.IsReferenced(config, root));

            File.WriteAllText(Path.Combine(api, "App.API.csproj"),
                $"""<Project><ItemGroup><PackageReference Include="{RuntimeBridge.PackageId}" Version="0.1.0" /></ItemGroup></Project>""");
            Assert.True(RuntimeBridge.IsReferenced(config, root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- seeding, against a real container ----------------------------------------------

    private sealed class RecordingSeeder(string name, int order, List<string> log) : ISeeder
    {
        public int Order => order;
        public string Name => name;

        public Task<int?> SeedAsync(CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return Task.FromResult<int?>(1);
        }
    }

    private static IHost HostWith(params ISeeder[] seeders) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                foreach (var seeder in seeders) services.AddSingleton(seeder);
            })
            .Build();

    [Fact]
    public async Task Seeders_run_in_Order_then_name()
    {
        var log = new List<string>();
        using var host = HostWith(
            new RecordingSeeder("Zebra", 1, log),
            new RecordingSeeder("Alpha", 1, log),
            new RecordingSeeder("First", 0, log));

        var handled = await CaptureAsync(() => host.RunForgeRuntimeAsync(["--forge:seed"]));

        Assert.True(handled.Handled);
        Assert.Equal(["First", "Alpha", "Zebra"], log);
    }

    [Fact]
    public async Task Only_filters_to_a_single_seeder()
    {
        var log = new List<string>();
        using var host = HostWith(new RecordingSeeder("Keep", 0, log), new RecordingSeeder("Skip", 1, log));

        await CaptureAsync(() => host.RunForgeRuntimeAsync(["--forge:seed", "--forge-only", "Keep"]));

        Assert.Equal(["Keep"], log);
    }

    [Fact]
    public async Task An_unknown_seeder_name_reports_what_is_registered()
    {
        var log = new List<string>();
        using var host = HostWith(new RecordingSeeder("RoleSeeder", 0, log));

        var result = await CaptureAsync(() => host.RunForgeRuntimeAsync(["--forge:seed", "--forge-only", "Nope"]));

        Assert.Contains("\"success\":false", result.Output);
        Assert.Contains("RoleSeeder", result.Output);
        // An expected condition travels as a message, not a stack trace.
        Assert.DoesNotContain("at Forge.Runtime", result.Output);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_normal_start_writes_nothing_and_returns_false()
    {
        using var host = HostWith(new RecordingSeeder("Any", 0, []));

        var result = await CaptureAsync(() => host.RunForgeRuntimeAsync(["--urls", "http://localhost:5000"]));

        Assert.False(result.Handled);
        Assert.Equal(string.Empty, result.Output);
    }

    private static async Task<(bool Handled, string Output)> CaptureAsync(Func<Task<bool>> action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var handled = await action();
            return (handled, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
