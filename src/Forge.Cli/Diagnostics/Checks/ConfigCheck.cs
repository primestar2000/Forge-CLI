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
/// Tier 2 availability, reported per capability rather than as one flag.
///
/// Three defects made this lie. It checked only the core package while invoke:* additionally
/// needs the Wolverine satellite, so doctor announced "Tier 2 - all commands available" and the
/// very next invoke:list refused. It matched the package id as a substring, so the satellite
/// alone satisfied the check for the core. And it never looked at Program.cs, so a project with
/// the package but no RunForgeRuntimeAsync hook — the state you land in after adding the package
/// by hand — was reported as working. All three observed on real projects.
///
/// A diagnostic that overstates what works is worse than none: it sends people looking in the
/// wrong place.
/// </summary>
public sealed class RuntimePackageCheck : IDoctorCheck
{
    public const string PackageId = "Pitechy.Forge.Runtime";
    public const string WolverinePackageId = "Pitechy.Forge.Runtime.Wolverine";

    public IEnumerable<CheckResult> Run(DoctorContext context)
    {
        if (!context.HasConfig)
        {
            yield return CheckResult.Skip("Forge.Runtime", "no config - cannot locate the API project");
            yield break;
        }

        var projects = context.AllProjectFiles;

        var core = ReferencesPackage(projects, PackageId);
        var wolverine = ReferencesPackage(projects, WolverinePackageId);
        var program = ReadProgram(context);

        var hooked = program.Contains("RunForgeRuntimeAsync", StringComparison.Ordinal);
        var registered = program.Contains("AddForgeWolverine", StringComparison.Ordinal);

        // Every gap below has the same remedy, and it is one command rather than the three
        // hand edits this used to print. The detail still names the specific thing missing —
        // knowing WHY is what makes the fix trustworthy.
        const string Remedy = "forge runtime:install";

        // ---- db:seed needs the core package AND the hook -------------------------------
        if (!core)
        {
            yield return CheckResult.Warn("db:seed",
                $"{PackageId} not referenced - unavailable (Tier 2)", Remedy);
        }
        else if (!hooked)
        {
            yield return CheckResult.Warn("db:seed",
                "package referenced but Program.cs has no RunForgeRuntimeAsync hook, " +
                "so the app never reports a result", Remedy);
        }
        else
        {
            yield return CheckResult.Pass("db:seed", "available");
        }

        // ---- invoke:* additionally needs the satellite AND its registration -------------
        if (!wolverine)
        {
            yield return CheckResult.Warn("invoke:*",
                $"{WolverinePackageId} not referenced - unavailable (Tier 2)", Remedy);
        }
        else if (!registered)
        {
            yield return CheckResult.Warn("invoke:*",
                "package referenced but AddForgeWolverine() is not called, so no verb handler is registered",
                Remedy);
        }
        else if (!hooked)
        {
            yield return CheckResult.Warn("invoke:*",
                "registered but Program.cs has no RunForgeRuntimeAsync hook", Remedy);
        }
        else
        {
            yield return CheckResult.Pass("invoke:*", "available");
        }
    }

    /// <summary>
    /// Matches the id inside the Include attribute, so "Pitechy.Forge.Runtime" is not satisfied
    /// by a reference to "Pitechy.Forge.Runtime.Wolverine".
    /// </summary>
    internal static bool ReferencesPackage(string projectFiles, string packageId) =>
        projectFiles.Contains($"\"{packageId}\"", StringComparison.OrdinalIgnoreCase);

    private static string ReadProgram(DoctorContext context)
    {
        try
        {
            var path = context.PathIn(context.Config!.ApiProject, "Program.cs");
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
