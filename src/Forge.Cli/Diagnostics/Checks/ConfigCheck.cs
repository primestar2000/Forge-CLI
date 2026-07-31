using Forge.Cli.Config;
using Forge.Cli.Stubs;
using Forge.Cli.Templates;

namespace Forge.Cli.Diagnostics.Checks;

/// <summary>
/// Config sanity: does it exist, is it a version we understand, does its template resolve, and do
/// the paths it names actually exist. Catches drift before a generator fails halfway through.
/// </summary>
public sealed class ConfigCheck : IDoctorCheck
{
    public IEnumerable<CheckResult> Run(DoctorContext context)
    {
        if (!context.HasConfig)
        {
            yield return CheckResult.Fail("forge.config.json", "not found",
                "forge init          # infer it from this solution");
            yield break;
        }

        var config = context.Config!;

        yield return CheckResult.Pass("forge.config.json", $"version {config.Version}, solution root {context.SolutionRoot}");

        var template = TemplateRegistry.Resolve(config.Template);
        yield return template is not null
            ? CheckResult.Pass("template", config.Template)
            : CheckResult.Fail("template", $"'{config.Template}' is not a known template",
                $"Known templates: {string.Join(", ", TemplateRegistry.Names)}");

        var problems = ConfigLoader.Validate(config, context.SolutionRoot);
        if (problems.Count == 0)
        {
            yield return CheckResult.Pass("config paths", "every configured path resolves");
        }
        else
        {
            foreach (var problem in problems)
                yield return CheckResult.Fail("config paths", problem,
                    "Fix forge.config.json, or re-run 'forge init --force' to re-infer it.");
        }
    }
}

/// <summary>
/// Tier 2 availability. Forge.Runtime is opt-in: its absence is never a failure, it just means
/// invoke:*, db:seed and runtime-mode route:list are unavailable.
/// </summary>
public sealed class RuntimePackageCheck : IDoctorCheck
{
    public const string PackageId = "Pitechy.Forge.Runtime";

    public IEnumerable<CheckResult> Run(DoctorContext context)
    {
        if (!context.HasConfig)
        {
            yield return CheckResult.Skip("Forge.Runtime", "no config - cannot locate the API project");
            yield break;
        }

        var referenced = context.AllProjectFiles.Contains(PackageId, StringComparison.OrdinalIgnoreCase);
        var apiProject = context.Config!.ApiProject;

        yield return referenced
            ? CheckResult.Pass("Forge.Runtime", "referenced - Tier 2 commands available")
            : CheckResult.Warn("Forge.Runtime",
                "not referenced - invoke:*, db:seed and full route:list unavailable (Tier 2)",
                $"dotnet add {apiProject} package {PackageId}");
    }
}

/// <summary>
/// Published stubs drift from the built-in defaults after a forge upgrade. Without this, stub:publish
/// is a one-way door: you customise once and silently stop receiving upstream improvements.
/// </summary>
public sealed class StubDriftCheck : IDoctorCheck
{
    public IEnumerable<CheckResult> Run(DoctorContext context)
    {
        if (!context.HasConfig || context.Stubs is null)
        {
            yield return CheckResult.Skip("stubs", "no config");
            yield break;
        }

        var stubDir = context.PathIn(context.Config!.StubOverridesPath);
        if (!Directory.Exists(stubDir))
        {
            yield return CheckResult.Pass("stubs", "using built-in defaults (none published)");
            yield break;
        }

        var drifts = StubDiffer.Compare(context.Stubs, []);
        if (drifts.Count == 0)
        {
            yield return CheckResult.Pass("stubs", "using built-in defaults (none published)");
            yield break;
        }

        // A published stub matching no built-in is dead weight: it will never be loaded, and the
        // team will wonder why their customisation silently stopped applying.
        foreach (var orphan in drifts.Where(d => d.Kind == DriftKind.Orphaned))
            yield return CheckResult.Warn("stubs",
                $"'{orphan.Name}' matches no built-in stub - it will never be used",
                "Probably renamed upstream. Compare with 'forge stub:diff'.");

        var noBaseline = drifts.Count(d => d.Kind == DriftKind.NoBaseline);
        if (noBaseline > 0)
            yield return CheckResult.Warn("stubs",
                $"{noBaseline} published stub(s) have no baseline, so drift cannot be attributed",
                "forge stub:publish --force   # re-publish to record a baseline");

        // Only upstream changes matter here. Local customisation is the entire point of
        // publishing and must never be reported as a problem.
        var unadopted = drifts.Where(d => d.Kind is DriftKind.UpstreamOnly or DriftKind.Both).ToList();

        yield return unadopted.Count == 0
            ? CheckResult.Pass("stubs", $"{drifts.Count} published, all current with built-in defaults")
            : CheckResult.Warn("stubs",
                $"{unadopted.Count} of {drifts.Count} published stub(s) have unadopted upstream changes: " +
                string.Join(", ", unadopted.Select(d => d.Name)),
                "forge stub:diff --verbosity d");
    }
}
