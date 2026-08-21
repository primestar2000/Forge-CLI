using Forge.Cli.Config;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;
using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests;

/// <summary>
/// runtime:install — wiring tier 2 into a solution that already exists.
///
/// This is the command three separate real projects needed. Each had a working solution, each
/// then wanted invoke:*, and each faced the same four hand edits. Everything asserted here is a
/// step one of them had to perform manually.
/// </summary>
public class RuntimeInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-ri-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string WolverineProgram = """
        using Microsoft.EntityFrameworkCore;
        using Wolverine;

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        builder.Services.AddScoped<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());
        builder.Services.AddInfrastructure();

        builder.Host.UseWolverine(options =>
        {
            options.Policies.AddMiddleware(typeof(RoleCheckMiddleware));
        });

        var app = builder.Build();

        app.UseHttpsRedirection();
        app.MapControllers();

        app.Run();
        """;

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="WolverineFx" Version="3.0.0" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>Lays down a solution shaped like one forge scaffolded, minus the runtime wiring.</summary>
    private void Scaffold(string program = WolverineProgram, bool withIdentityTypes = true)
    {
        var api = Path.Combine(_root, "src", "App.API");
        var application = Path.Combine(_root, "src", "App.Application");
        Directory.CreateDirectory(api);
        Directory.CreateDirectory(application);

        File.WriteAllText(Path.Combine(api, "App.API.csproj"), Csproj);
        File.WriteAllText(Path.Combine(api, "Program.cs"), program);

        if (withIdentityTypes)
        {
            File.WriteAllText(Path.Combine(application, "CurrentUser.cs"),
                "namespace App.Application;\npublic interface ICurrentUser { }\n" +
                "public interface ICurrentUserSetter { }\npublic class CurrentUser { }");
        }

        File.WriteAllText(Path.Combine(_root, "forge.config.json"), """
            {
              "version": 1,
              "template": "onion-wolverine-erroror",
              "apiProject": "src/App.API",
              "applicationProject": "src/App.Application"
            }
            """);
    }

    private PlanResult Plan(string? version = "9.9.9")
    {
        var loaded = ConfigLoader.Load(_root);
        Assert.True(loaded.Ok, loaded.Error);

        var ctx = new TemplateContext(
            loaded.Config!, loaded.SolutionRoot!, CodeStyle.Default,
            new StubRepository(loaded.SolutionRoot!, loaded.Config!.StubOverridesPath, "OnionWolverineErrorOr"),
            force: false);

        return RuntimeInstallScaffold.Plan(ctx, version);
    }

    private static string PatchedText(PlanResult result, string endsWith) =>
        result.Plan.Actions.OfType<FileAction.Patch>()
            .Single(a => a.Path.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
            .After;

    /// <summary>Applies the plan so a second run sees the first run's output — the real sequence.</summary>
    private void Apply(PlanResult result)
    {
        Assert.True(result.Ok, result.Error);
        var execution = new PlanExecutor().Apply(result.Plan);
        Assert.True(execution.Ok, execution.Error);
    }

    [Fact]
    public void It_adds_both_packages_at_the_requested_version()
    {
        Scaffold();

        var csproj = PatchedText(Plan(), ".csproj");

        Assert.Contains("""<PackageReference Include="Pitechy.Forge.Runtime" Version="9.9.9" />""", csproj);
        Assert.Contains("""<PackageReference Include="Pitechy.Forge.Runtime.Wolverine" Version="9.9.9" />""", csproj);

        // Added to the existing ItemGroup, not a new one, and the existing reference survives.
        Assert.Contains("WolverineFx", csproj);
        Assert.Equal(1, csproj.Split("<ItemGroup>").Length - 1);
    }

    [Fact]
    public void It_wires_the_hook_the_registration_and_the_usings()
    {
        Scaffold();

        var program = PatchedText(Plan(), "Program.cs");

        Assert.Contains("using Forge.Runtime;", program);
        Assert.Contains("using Forge.Runtime.Wolverine;", program);
        Assert.Contains("builder.Services.AddForgeWolverine();", program);
        Assert.Contains("if (await app.RunForgeRuntimeAsync(args)) return;", program);
    }

    /// <summary>
    /// Order is the whole correctness argument. The hook must land after the host is built (it
    /// needs `app`) and before app.Run(), and the registrations must land before Build() or the
    /// container is already closed.
    /// </summary>
    [Fact]
    public void The_hook_lands_between_Build_and_Run()
    {
        Scaffold();

        var program = PatchedText(Plan(), "Program.cs");

        var register = program.IndexOf("AddForgeWolverine", StringComparison.Ordinal);
        var build = program.IndexOf("builder.Build()", StringComparison.Ordinal);
        var hook = program.IndexOf("RunForgeRuntimeAsync", StringComparison.Ordinal);
        var run = program.IndexOf("app.Run();", StringComparison.Ordinal);

        Assert.True(register < build, "AddForgeWolverine must be registered before the host is built");
        Assert.True(build < hook, "the hook needs `app`, so it must follow Build()");
        Assert.True(hook < run, "the hook must short-circuit before the web server starts");
    }

    /// <summary>
    /// Wolverine opens a scope per message, so a scoped identity is not the instance the handler
    /// resolves and every guarded message is denied. The override is appended rather than
    /// replacing the developer's own lines — last registration wins.
    /// </summary>
    [Fact]
    public void It_overrides_the_identity_lifetime_without_touching_the_existing_registrations()
    {
        Scaffold();

        var program = PatchedText(Plan(), "Program.cs");

        Assert.Contains("if (ForgeRuntimeExtensions.IsForgeInvocation(args))", program);
        Assert.Contains("builder.Services.AddSingleton<ICurrentUserSetter>", program);

        // The developer's scoped registrations are still there, untouched.
        Assert.Contains("builder.Services.AddScoped<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());", program);

        Assert.True(
            program.IndexOf("AddScoped<ICurrentUserSetter>", StringComparison.Ordinal) <
            program.IndexOf("AddSingleton<ICurrentUserSetter>", StringComparison.Ordinal),
            "the override only works if it is registered last");
    }

    /// <summary>
    /// A brownfield app without these types would not compile if forge emitted registrations
    /// referencing them. Wiring that breaks the build is worse than wiring that stops short.
    ///
    /// "Without these types" means Program.cs does not register them — that is the signal forge
    /// reads, because the override is appended to Program.cs and has to compile there.
    /// </summary>
    [Fact]
    public void It_skips_the_identity_override_when_the_types_do_not_exist()
    {
        Scaffold(WolverineProgram
            .Replace("builder.Services.AddScoped<CurrentUser>();\n", string.Empty, StringComparison.Ordinal)
            .Replace("builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());\n", string.Empty, StringComparison.Ordinal)
            .Replace("builder.Services.AddScoped<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());\n", string.Empty, StringComparison.Ordinal),
            withIdentityTypes: false);

        var program = PatchedText(Plan(), "Program.cs");

        Assert.DoesNotContain("AddSingleton<ICurrentUserSetter>", program);
        Assert.Contains("RunForgeRuntimeAsync", program);   // core wiring still happens
    }

    /// <summary>
    /// The regression this detection was rewritten for.
    ///
    /// forge's own template declares ICurrentUserSetter in the APPLICATION project and CurrentUser
    /// in the API project. While the check looked both up in the application project it was never
    /// satisfied on a forge-generated solution — no override was emitted, Wolverine's per-message
    /// scope handed the handler a different ICurrentUser than the runtime had set, and
    /// invoke:run --as-role Admin was refused with "role 'Guest' cannot execute". Observed end to
    /// end before it was fixed.
    /// </summary>
    [Fact]
    public void It_overrides_the_identity_when_CurrentUser_lives_in_the_api_project()
    {
        Scaffold(withIdentityTypes: false);

        var api = Path.Combine(_root, "src", "App.API");
        File.WriteAllText(Path.Combine(api, "CurrentUser.cs"),
            "namespace App.API;\npublic class CurrentUser : ICurrentUserSetter { }");

        var application = Path.Combine(_root, "src", "App.Application");
        File.WriteAllText(Path.Combine(application, "ICurrentUser.cs"),
            "namespace App.Application;\npublic interface ICurrentUser { }\n" +
            "public interface ICurrentUserSetter : ICurrentUser { }");

        var program = PatchedText(Plan(), "Program.cs");

        Assert.Contains("if (ForgeRuntimeExtensions.IsForgeInvocation(args))", program);
        Assert.Contains("builder.Services.AddSingleton<ICurrentUserSetter>", program);
    }

    /// <summary>
    /// AddForgeWolverine() in an app with no bus does not compile. db:seed does not need
    /// Wolverine, so the core half is still wired.
    /// </summary>
    [Fact]
    public void An_app_without_Wolverine_gets_only_the_core_runtime()
    {
        Scaffold("""
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.Run();
            """, withIdentityTypes: false);

        var result = Plan();

        Assert.DoesNotContain("Pitechy.Forge.Runtime.Wolverine", PatchedText(result, ".csproj"));
        Assert.Contains("Pitechy.Forge.Runtime", PatchedText(result, ".csproj"));

        var program = PatchedText(result, "Program.cs");
        Assert.DoesNotContain("AddForgeWolverine", program);
        Assert.DoesNotContain("using Forge.Runtime.Wolverine;", program);
        Assert.Contains("if (await app.RunForgeRuntimeAsync(args)) return;", program);
    }

    /// <summary>Invariant 6. Running it twice must change nothing the second time.</summary>
    [Fact]
    public void Running_it_twice_is_a_no_op()
    {
        Scaffold();
        Apply(Plan());

        var second = Plan();

        Assert.True(second.Ok, second.Error);
        Assert.True(second.Plan.IsEntirelySkipped,
            "second run wrote: " + string.Join(", ", second.Plan.Writes.Select(w => w.Path)));
    }

    /// <summary>A solution someone already wired by hand must be recognised, not duplicated.</summary>
    [Fact]
    public void A_hand_wired_solution_is_recognised_as_already_done()
    {
        Scaffold(WolverineProgram
            .Replace("var app = builder.Build();",
                "builder.Services.AddForgeWolverine();\n\nvar app = builder.Build();\n" +
                "if (await app.RunForgeRuntimeAsync(args)) return;"));

        var program = Plan().Plan.Actions
            .OfType<FileAction.Patch>()
            .SingleOrDefault(a => a.Path.EndsWith("Program.cs", StringComparison.Ordinal));

        // The usings are still missing in this hand-wired file, so a patch is legitimate —
        // but it must not add a second hook or a second registration.
        var text = program?.After ?? File.ReadAllText(Path.Combine(_root, "src", "App.API", "Program.cs"));

        Assert.Equal(1, Occurrences(text, "RunForgeRuntimeAsync"));
        Assert.Equal(1, Occurrences(text, "AddForgeWolverine"));
    }

    /// <summary>
    /// The state a solution scaffolded by an older --with-runtime is in: identity swap present,
    /// Wolverine half absent. Found on a real project — a batch-level idempotency marker keyed
    /// on AddForgeWolverine sees "not present", inserts the whole batch, and duplicates the
    /// identity block. Each statement must be checked on its own.
    /// </summary>
    [Fact]
    public void A_partially_wired_solution_gains_only_the_missing_half()
    {
        Scaffold(WolverineProgram.Replace(
            "builder.Services.AddInfrastructure();",
            """
            builder.Services.AddInfrastructure();

            if (ForgeRuntimeExtensions.IsForgeInvocation(args))
            {
                builder.Services.AddSingleton<CurrentUser>();
                builder.Services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
                builder.Services.AddSingleton<ICurrentUserSetter>(sp => sp.GetRequiredService<CurrentUser>());
            }
            """));

        var program = PatchedText(Plan(), "Program.cs");

        Assert.Equal(1, Occurrences(program, "IsForgeInvocation(args)"));
        Assert.Equal(1, Occurrences(program, "AddSingleton<ICurrentUserSetter>"));
        Assert.Equal(1, Occurrences(program, "AddForgeWolverine"));
    }

    /// <summary>
    /// csproj files conventionally indent with two spaces; CodeStyle.IndentUnit describes C#
    /// and is four. Composing the indent from it produces a line that does not line up.
    /// </summary>
    [Fact]
    public void The_added_reference_matches_its_siblings_indentation()
    {
        Scaffold();

        var added = PatchedText(Plan(), ".csproj")
            .Split('\n')
            .First(l => l.Contains("Pitechy.Forge.Runtime\"", StringComparison.Ordinal));

        Assert.StartsWith("    <PackageReference", added);
    }

    [Fact]
    public void A_missing_Program_cs_names_the_file_and_the_edit()
    {
        Scaffold();
        File.Delete(Path.Combine(_root, "src", "App.API", "Program.cs"));

        var result = Plan();

        Assert.False(result.Ok);
        Assert.Contains("Program.cs", result.Error);
        Assert.Contains("RunForgeRuntimeAsync", result.Error);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
