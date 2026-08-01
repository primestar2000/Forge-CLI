using Forge.Cli.Cli;
using Forge.Cli.Config;

namespace Forge.Cli.Tests;

/// <summary>
/// Argument construction is the entire value of the db:* namespace — getting --project /
/// --startup-project wrong is the usual reason dotnet-ef fails in a layered solution. It is pure,
/// so it is tested directly rather than by launching processes.
/// </summary>
public class EfToolTests
{
    private static ForgeConfig Config() => new()
    {
        SolutionName = "Shop",
        InfrastructureProject = "src/Shop.Infrastructure",
        ApiProject = "src/Shop.API",
        DbContextName = "ShopDbContext",
        InfrastructureMigrationsPath = "Persistence/Migrations"
    };

    /// <summary>Value of the argument immediately following <paramref name="flag"/>.</summary>
    private static string ValueAfter(EfCommand command, string flag)
    {
        var args = command.Arguments.ToList();
        var index = args.IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"'{flag}' missing from: {command.Display}");
        return args[index + 1];
    }

    private static void AssertTargetsResolved(EfCommand command)
    {
        // The DbContext lives in Infrastructure; the host that configures it lives in the API.
        Assert.Equal("src/Shop.Infrastructure", ValueAfter(command, "--project"));
        Assert.Equal("src/Shop.API", ValueAfter(command, "--startup-project"));
        Assert.Equal("ShopDbContext", ValueAfter(command, "--context"));
    }

    [Fact]
    public void MigrationsAdd_passes_name_targets_and_output_dir()
    {
        var command = EfTool.MigrationsAdd(Config(), "AddOrderTable");

        Assert.Equal(["ef", "migrations", "add", "AddOrderTable"], command.Arguments.Take(4));
        AssertTargetsResolved(command);
        Assert.Equal("Persistence/Migrations", ValueAfter(command, "--output-dir"));
    }

    [Fact]
    public void DatabaseUpdate_without_a_target_applies_everything_pending()
    {
        var command = EfTool.DatabaseUpdate(Config());

        Assert.Equal(["ef", "database", "update"], command.Arguments.Take(3));
        Assert.DoesNotContain(command.Arguments, a => a == "0");
        AssertTargetsResolved(command);
    }

    [Fact]
    public void DatabaseUpdate_with_a_target_places_it_before_the_flags()
    {
        var command = EfTool.DatabaseUpdate(Config(), "AddOrderTable");

        Assert.Equal(["ef", "database", "update", "AddOrderTable"], command.Arguments.Take(4));
        AssertTargetsResolved(command);
    }

    [Fact]
    public void DatabaseDrop_is_non_interactive()
    {
        var command = EfTool.DatabaseDrop(Config());

        Assert.Equal(["ef", "database", "drop"], command.Arguments.Take(3));
        // Without --force ef prompts, and the CLI runs non-interactively.
        Assert.Contains("--force", command.Arguments);
        AssertTargetsResolved(command);
    }

    [Fact]
    public void MigrationsList_resolves_targets()
    {
        var command = EfTool.MigrationsList(Config());

        Assert.Equal(["ef", "migrations", "list"], command.Arguments.Take(3));
        AssertTargetsResolved(command);
    }

    [Fact]
    public void DbContextName_falls_back_to_the_solution_name()
    {
        var config = Config();
        config.DbContextName = "";

        Assert.Contains("ShopDbContext", EfTool.MigrationsList(config).Arguments);
    }

    /// <summary>
    /// Regression: Display once prepended "ef" that Arguments did not contain, so --dry-run
    /// printed a correct command while the process was launched without it and failed with
    /// "dotnet-migrations does not exist". Display and execution must share one source of truth.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Display_exactly_matches_what_would_be_executed(EfCommand command)
    {
        Assert.Equal("ef", command.Arguments[0]);
        Assert.StartsWith("dotnet ef ", command.Display);

        // Reconstructing the display from the argument list must round-trip.
        Assert.Equal("dotnet " + string.Join(" ", command.Arguments), command.Display);
    }

    public static TheoryData<EfCommand> AllCommands() =>
    [
        EfTool.MigrationsAdd(Config(), "AddOrderTable"),
        EfTool.DatabaseUpdate(Config()),
        EfTool.DatabaseUpdate(Config(), "0"),
        EfTool.MigrationsList(Config()),
        EfTool.DatabaseDrop(Config())
    ];

    [Fact]
    public void Display_quotes_arguments_containing_spaces()
    {
        var config = Config();
        config.InfrastructureProject = "src/My Solution.Infrastructure";

        Assert.Contains("\"src/My Solution.Infrastructure\"", EfTool.MigrationsList(config).Display);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("production", true)]
    [InlineData("Staging", false)]
    [InlineData(null, false)]
    public void Production_is_detected_case_insensitively(string? value, bool expected)
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", value);

            Assert.Equal(expected, EfTool.IsProduction(out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
        }
    }

    [Fact]
    public void DOTNET_ENVIRONMENT_is_honoured_when_ASPNETCORE_ENVIRONMENT_is_unset()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

            Assert.True(EfTool.IsProduction(out var environment));
            Assert.Equal("Production", environment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
        }
    }
}
