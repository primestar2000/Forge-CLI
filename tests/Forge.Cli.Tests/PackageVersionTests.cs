using Forge.Cli.Templates.OnionWolverineErrorOr;

namespace Forge.Cli.Tests;

/// <summary>
/// The scaffold's package versions have to be a coherent set per target framework.
///
/// These are cheap assertions guarding an expensive failure: the previous pins restored, built
/// and ran, so every gate passed, and the solution only broke when a user added a provider
/// package — at which point no per-package nudge could fix it, because WolverineFx 3.6.1 caps
/// every Microsoft.Extensions.* package below 10.0.0 while a net10.0 target resolves them at
/// 10.x. Reported from a real project.
/// </summary>
public class PackageVersionTests
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public void Every_supported_framework_has_a_version_set(string targetFramework)
    {
        var set = PackageVersions.For(targetFramework);

        Assert.False(string.IsNullOrWhiteSpace(set.EfCore));
        Assert.False(string.IsNullOrWhiteSpace(set.Wolverine));
        Assert.False(string.IsNullOrWhiteSpace(set.SqlitePclRaw));
    }

    /// <summary>
    /// EF Core's major must not exceed the framework's. EF Core 10 requires net10.0, so pairing
    /// it with net8.0 produces a package that will not restore at all.
    /// </summary>
    [Theory]
    [InlineData("net8.0", 8)]
    [InlineData("net9.0", 9)]
    [InlineData("net10.0", 10)]
    public void EfCore_never_outruns_the_target_framework(string targetFramework, int frameworkMajor)
    {
        var efMajor = int.Parse(PackageVersions.For(targetFramework).EfCore.Split('.')[0]);

        Assert.True(efMajor <= frameworkMajor,
            $"{targetFramework} pins EF Core {efMajor}.x, which cannot restore on it.");
    }

    /// <summary>
    /// The specific regression. WolverineFx 3.x caps Microsoft.Extensions.* below 10.0.0 on
    /// every one of its target framework groups, so it can never appear beside a net10.0 target.
    /// </summary>
    [Fact]
    public void No_framework_pins_a_Wolverine_that_caps_Extensions_below_ten()
    {
        foreach (var targetFramework in new[] { "net8.0", "net9.0", "net10.0" })
        {
            var major = int.Parse(PackageVersions.For(targetFramework).Wolverine.Split('.')[0]);

            Assert.True(major >= 5,
                $"{targetFramework} pins WolverineFx {major}.x. 3.x and 4.x cap " +
                "Microsoft.Extensions.* below 10.0.0, which makes the graph unable to move.");
        }
    }

    /// <summary>
    /// Wolverine 6 has no net8.0 assets — pinning it there is an NU1202, not a warning.
    /// </summary>
    [Fact]
    public void Net8_stays_on_the_last_Wolverine_that_supports_it()
    {
        var major = int.Parse(PackageVersions.For("net8.0").Wolverine.Split('.')[0]);

        Assert.Equal(5, major);
    }

    /// <summary>
    /// A TFM forge has not seen yet gets the newest known-good set. Refusing to scaffold on a
    /// future framework would be worse than being one generation behind on it.
    /// </summary>
    [Fact]
    public void An_unknown_framework_falls_back_to_the_newest_set()
    {
        Assert.Equal(PackageVersions.For("net10.0"), PackageVersions.For("net11.0"));
    }
}
