using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Cli.Cli;
using Forge.Cli.Planning;

namespace Forge.Cli.Stubs;

/// <summary>
/// stub:publish — copy built-in stubs into the solution so a team can change the generated shape
/// without forking the tool.
///
/// Publishing also writes a pristine BASELINE copy and a version marker. Without those,
/// stub:diff can only say "yours differs from the current built-in", which the team already
/// knows because they customised it. The baseline is what makes a real three-way diff possible,
/// entirely offline.
/// </summary>
public static class StubPublisher
{
    public static PlanResult Plan(StubRepository stubs, IReadOnlyList<string> only, bool force)
    {
        var available = stubs.BuiltInStubNames().ToList();

        var selected = only.Count == 0
            ? available
            : available.Where(n => only.Any(o => Matches(n, o))).ToList();

        if (only.Count > 0 && selected.Count == 0)
        {
            return PlanResult.UsageError(
                $"No built-in stub matches {string.Join(", ", only.Select(o => $"'{o}'"))}." + Environment.NewLine +
                $"  -> Available: {string.Join(", ", available)}");
        }

        var plan = GenerationPlan.Empty;

        foreach (var name in selected)
        {
            var content = stubs.LoadBuiltIn(name);
            var target = stubs.OverridePath(name);
            var baseline = stubs.BaselinePath(name);

            if (File.Exists(target) && !force)
            {
                plan = plan.With(new FileAction.Skip(target, "already published (use --force to re-publish)"));
                continue;
            }

            plan = plan.With(new FileAction.Create(target, content));

            // The baseline always tracks the version being published, so "yours" is measured
            // against the built-in you actually started from.
            plan = plan.With(new FileAction.Create(baseline, content));
        }

        if (plan.Actions.All(a => a is FileAction.Skip)) return PlanResult.Success(plan);

        var marker = new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["publishedUtc"] = DateTime.UtcNow.ToString("O"),
            ["template"] = stubs.TemplateName
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

        return PlanResult.Success(plan.With(new FileAction.Create(stubs.VersionMarkerPath, marker)));
    }

    private static bool Matches(string stubName, string pattern)
    {
        if (stubName.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;

        // Allow the bare file name for a nested stub: --only Program.cs.txt finds Solution/Program.cs.txt.
        var leaf = stubName[(stubName.LastIndexOf('/') + 1)..];
        return leaf.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

public enum DriftKind
{
    /// <summary>Published stub is identical to the built-in it came from and to today's built-in.</summary>
    Unchanged,

    /// <summary>Only the team changed it. Safe.</summary>
    LocalOnly,

    /// <summary>Only forge changed it — the team is missing an upstream improvement.</summary>
    UpstreamOnly,

    /// <summary>Both changed. May need a manual merge.</summary>
    Both,

    /// <summary>Published but with no baseline recorded, so only a two-way comparison is possible.</summary>
    NoBaseline,

    /// <summary>Published stub matches no built-in — probably renamed upstream, and now dead.</summary>
    Orphaned
}

public sealed record StubDrift(
    string Name,
    DriftKind Kind,
    int LocalAdded,
    int LocalRemoved,
    int UpstreamAdded,
    int UpstreamRemoved,
    bool Overlapping)
{
    public bool NeedsAttention => Kind is DriftKind.UpstreamOnly or DriftKind.Both or DriftKind.Orphaned;
}

public static class StubDiffer
{
    public static IReadOnlyList<StubDrift> Compare(StubRepository stubs, IReadOnlyList<string> only)
    {
        var builtIn = stubs.BuiltInStubNames().ToHashSet(StringComparer.Ordinal);
        var published = stubs.PublishedStubNames()
            .Where(n => only.Count == 0 || only.Any(o => n.Equals(o, StringComparison.OrdinalIgnoreCase)
                                                      || Path.GetFileName(n).Equals(o, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var results = new List<StubDrift>();

        foreach (var name in published)
        {
            if (!builtIn.Contains(name))
            {
                results.Add(new StubDrift(name, DriftKind.Orphaned, 0, 0, 0, 0, false));
                continue;
            }

            var yours = Read(stubs.OverridePath(name));
            var current = Normalise(stubs.LoadBuiltIn(name));

            // Short-circuit: if the published copy already matches today's built-in there is
            // nothing to adopt, whatever the baseline says. Without this, a baseline recorded at
            // an older version reports phantom drift on a stub nobody ever edited.
            if (yours == current)
            {
                results.Add(new StubDrift(name, DriftKind.Unchanged, 0, 0, 0, 0, false));
                continue;
            }

            if (!stubs.HasBaseline(name))
            {
                var (a, r) = Count(current, yours);
                results.Add(new StubDrift(name, DriftKind.NoBaseline, a, r, 0, 0, false));
                continue;
            }

            var baseline = Read(stubs.BaselinePath(name));

            var (localAdded, localRemoved) = Count(baseline, yours);
            var (upstreamAdded, upstreamRemoved) = Count(baseline, current);

            var localChanged = localAdded + localRemoved > 0;
            var upstreamChanged = upstreamAdded + upstreamRemoved > 0;

            var kind = (localChanged, upstreamChanged) switch
            {
                (false, false) => DriftKind.Unchanged,
                (true, false) => DriftKind.LocalOnly,
                (false, true) => DriftKind.UpstreamOnly,
                _ => DriftKind.Both
            };

            results.Add(new StubDrift(
                name, kind, localAdded, localRemoved, upstreamAdded, upstreamRemoved,
                Overlaps(baseline, yours, current)));
        }

        return results;
    }

    /// <summary>
    /// Renders the two halves of the three-way comparison: what the team changed, and what
    /// forge changed, both measured from the same baseline.
    /// </summary>
    public static (string Local, string Upstream) Render(StubRepository stubs, string name)
    {
        var baseline = stubs.HasBaseline(name) ? Read(stubs.BaselinePath(name)) : Normalise(stubs.LoadBuiltIn(name));
        var yours = Read(stubs.OverridePath(name));
        var current = Normalise(stubs.LoadBuiltIn(name));

        return (LineDiff.Render(baseline, yours), LineDiff.Render(baseline, current));
    }

    /// <summary>
    /// A line the team edited that upstream ALSO edited cannot be adopted mechanically. Detected
    /// by intersecting the sets of baseline lines each side removed.
    /// </summary>
    private static bool Overlaps(string baseline, string yours, string current)
    {
        var removedByYou = Removed(baseline, yours);
        if (removedByYou.Count == 0) return false;

        var removedByUpstream = Removed(baseline, current);
        return removedByUpstream.Count != 0 && removedByYou.Overlaps(removedByUpstream);
    }

    private static HashSet<string> Removed(string before, string after) =>
        LineDiff.Compute(before, after)
            .Where(l => l.Kind == DiffKind.Removed && l.Text.Trim().Length > 0)
            .Select(l => l.Text.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static (int Added, int Removed) Count(string before, string after)
    {
        var lines = LineDiff.Compute(before, after);
        return (lines.Count(l => l.Kind == DiffKind.Added),
                lines.Count(l => l.Kind == DiffKind.Removed));
    }

    private static string Read(string path)
    {
        try { return Normalise(File.ReadAllText(path)); } catch { return string.Empty; }
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
