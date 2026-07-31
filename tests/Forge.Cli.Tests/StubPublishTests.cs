using Forge.Cli.Cli;
using Forge.Cli.Planning;
using Forge.Cli.Stubs;

namespace Forge.Cli.Tests;

public class StubPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-stub-" + Guid.NewGuid().ToString("N")[..8]);

    private StubRepository Repo() => new(_root, ".forge/stubs", "OnionWolverineErrorOr");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Applies a publish plan for real so the diff tests have something to look at.</summary>
    private void Publish(params string[] only)
    {
        var result = StubPublisher.Plan(Repo(), only, force: false);
        Assert.True(result.Ok, result.Error);
        Assert.True(new PlanExecutor().Apply(result.Plan).Ok);
    }

    private void Edit(string name, Func<string, string> transform) =>
        File.WriteAllText(Repo().OverridePath(name), transform(File.ReadAllText(Repo().OverridePath(name))));

    private void RewriteBaseline(string name, Func<string, string> transform) =>
        File.WriteAllText(Repo().BaselinePath(name), transform(File.ReadAllText(Repo().BaselinePath(name))));

    [Fact]
    public void Publishing_writes_the_stub_a_baseline_and_a_version_marker()
    {
        var result = StubPublisher.Plan(Repo(), ["Repository.cs.txt"], force: false);
        Assert.True(result.Ok, result.Error);

        var paths = result.Plan.Actions.Select(a => a.Path.Replace('\\', '/')).ToList();

        Assert.Contains(paths, p => p.EndsWith(".forge/stubs/Repository.cs.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(".forge/stubs/.baseline/Repository.cs.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(".forge-version", StringComparison.Ordinal));
    }

    /// <summary>Nested stub names must round-trip; the folder layout is preserved under .forge/stubs.</summary>
    [Fact]
    public void Nested_stub_names_keep_their_folder()
    {
        var names = Repo().BuiltInStubNames().ToList();
        Assert.Contains("Solution/Program.cs.txt", names);

        var result = StubPublisher.Plan(Repo(), ["Solution/Program.cs.txt"], force: false);
        Assert.True(result.Ok, result.Error);
        Assert.Contains(result.Plan.Actions.Select(a => a.Path.Replace('\\', '/')),
            p => p.EndsWith(".forge/stubs/Solution/Program.cs.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Publishing_an_unknown_stub_is_a_usage_error_listing_what_exists()
    {
        var result = StubPublisher.Plan(Repo(), ["Nope.cs.txt"], force: false);

        Assert.False(result.Ok);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Repository.cs.txt", result.Error);
    }

    [Fact]
    public void Republishing_skips_unless_forced()
    {
        Publish("Repository.cs.txt");

        var again = StubPublisher.Plan(Repo(), ["Repository.cs.txt"], force: false);
        Assert.All(again.Plan.Actions, a => Assert.IsType<FileAction.Skip>(a));

        var forced = StubPublisher.Plan(Repo(), ["Repository.cs.txt"], force: true);
        Assert.Contains(forced.Plan.Actions, a => a is FileAction.Create);
    }

    [Fact]
    public void An_untouched_published_stub_reports_unchanged()
    {
        Publish("Repository.cs.txt");

        var drift = StubDiffer.Compare(Repo(), []).Single();
        Assert.Equal(DriftKind.Unchanged, drift.Kind);
        Assert.False(drift.NeedsAttention);
    }

    /// <summary>
    /// Local customisation is the entire point of publishing, so it must never be reported as
    /// something needing attention.
    /// </summary>
    [Fact]
    public void Local_customisation_alone_needs_no_attention()
    {
        Publish("Repository.cs.txt");
        Edit("Repository.cs.txt", c => "// Copyright (c) Pitechy.\n" + c);

        var drift = StubDiffer.Compare(Repo(), []).Single();

        Assert.Equal(DriftKind.LocalOnly, drift.Kind);
        Assert.Equal(1, drift.LocalAdded);
        Assert.False(drift.NeedsAttention);
    }

    /// <summary>
    /// The whole reason stub:diff exists: an upstream improvement the team has not adopted,
    /// which their published copy would otherwise hide forever.
    /// </summary>
    [Fact]
    public void Unadopted_upstream_change_is_flagged()
    {
        Publish("Repository.cs.txt");
        // Make the baseline look like an older built-in, so upstream appears to have changed.
        RewriteBaseline("Repository.cs.txt", c => c.Replace(".AsNoTracking()", ".AsNoTracking()\n    .OrderBy(e => e.Id)"));
        Edit("Repository.cs.txt", c => "// Copyright (c) Pitechy.\n" + c);

        var drift = StubDiffer.Compare(Repo(), []).Single();

        Assert.Equal(DriftKind.Both, drift.Kind);
        Assert.True(drift.NeedsAttention);
    }

    /// <summary>
    /// Regression: a baseline recorded at an older version must not produce phantom drift on a
    /// stub nobody edited. If the published copy already equals today's built-in, there is
    /// nothing to adopt whatever the baseline says.
    /// </summary>
    [Fact]
    public void Stale_baseline_does_not_invent_drift_on_an_unedited_stub()
    {
        Publish("Entity.cs.txt");
        RewriteBaseline("Entity.cs.txt", c => c.Replace("public Guid Id { get; set; }",
            "public Guid Id { get; set; }\n    public DateTime CreatedAt { get; set; }"));

        var drift = StubDiffer.Compare(Repo(), []).Single();

        Assert.Equal(DriftKind.Unchanged, drift.Kind);
        Assert.False(drift.NeedsAttention);
    }

    [Fact]
    public void A_published_stub_matching_no_builtin_is_reported_as_orphaned()
    {
        Publish("Repository.cs.txt");
        var stubs = Repo();
        File.WriteAllText(Path.Combine(stubs.StubDirectory, "Renamed.cs.txt"), "// stale");

        var drift = StubDiffer.Compare(stubs, []).Single(d => d.Name == "Renamed.cs.txt");

        Assert.Equal(DriftKind.Orphaned, drift.Kind);
        Assert.True(drift.NeedsAttention);
    }

    /// <summary>
    /// Regression: a licence header is the most common stub customisation there is. An earlier
    /// implementation stripped every leading comment as "token header" documentation and ate it
    /// silently. Only "// forge:" directive lines may be removed.
    /// </summary>
    [Fact]
    public void A_licence_header_added_to_a_stub_survives_into_generated_output()
    {
        Publish("Errors.cs.txt");
        Edit("Errors.cs.txt", c => "// Copyright (c) Pitechy. All rights reserved.\n" + c);

        var rendered = Repo().Render("Errors.cs.txt", new Dictionary<string, string>
        {
            ["Usings"] = "",
            ["Namespace"] = "App.Errors",
            ["Entity"] = "Order",
            ["EntityCamelSpaced"] = "order"
        });

        Assert.StartsWith("// Copyright (c) Pitechy. All rights reserved.", rendered);
        Assert.DoesNotContain("forge:tokens", rendered);
    }

    [Fact]
    public void Published_stubs_win_over_built_in_ones()
    {
        Publish("Errors.cs.txt");
        Edit("Errors.cs.txt", c => c.Replace("public static partial class Errors", "public static partial class Errors // customised"));

        var rendered = Repo().Render("Errors.cs.txt", new Dictionary<string, string>
        {
            ["Usings"] = "",
            ["Namespace"] = "App.Errors",
            ["Entity"] = "Order",
            ["EntityCamelSpaced"] = "order"
        });

        Assert.Contains("// customised", rendered);
    }
}
