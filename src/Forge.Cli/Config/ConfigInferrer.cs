using Microsoft.CodeAnalysis.CSharp;

namespace Forge.Cli.Config;

public enum Confidence { High, Medium, Low }

/// <summary>One inferred setting, with the evidence that produced it so a human can audit it.</summary>
public sealed record Inference(string Field, string Value, Confidence Confidence, string Evidence);

public sealed record InferenceResult(
    ForgeConfig Config,
    IReadOnlyList<Inference> Inferences,
    string SolutionRoot,
    string? Error = null)
{
    public bool Ok => Error is null;
    public bool HasLowConfidence => Inferences.Any(i => i.Confidence == Confidence.Low);
}

/// <summary>
/// Infers forge.config.json from an existing solution — the brownfield adoption path, and the
/// difference between forge being a greenfield-only toy and something a team can actually adopt.
///
/// Every inference records its evidence. Guessing silently would be worse than not guessing.
/// </summary>
public static class ConfigInferrer
{
    public static InferenceResult Infer(string startDirectory)
    {
        var solutionFile = Fs.FindByPatternUpward(startDirectory, "*.sln") ?? Fs.FindByPatternUpward(startDirectory, "*.slnx");
        var root = solutionFile is not null
            ? Path.GetDirectoryName(solutionFile)!
            : Path.GetFullPath(startDirectory);

        var projects = Fs.Files(root, "*.csproj")
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        if (projects.Count == 0)
        {
            return new InferenceResult(new ForgeConfig(), [], root,
                $"No .csproj files found under {root}." + Environment.NewLine +
                "  -> Run 'forge init' from inside a solution, or 'forge make:solution -n <Name>' to scaffold one.");
        }

        var inferences = new List<Inference>();
        var index = SourceIndex.Build(root);

        var config = new ForgeConfig
        {
            Schema = "https://raw.githubusercontent.com/Pitechy/forge/main/schema/forge.config.v1.json",
            Version = ForgeConfig.CurrentVersion,
            Template = "onion-wolverine-erroror"
        };

        // ---- solution name
        config.SolutionName = solutionFile is not null
            ? Path.GetFileNameWithoutExtension(solutionFile)
            : new DirectoryInfo(root).Name;

        inferences.Add(new Inference("solutionName", config.SolutionName,
            solutionFile is not null ? Confidence.High : Confidence.Medium,
            solutionFile is not null ? Path.GetFileName(solutionFile) : "directory name (no .sln found)"));

        // ---- projects, by name suffix
        var domain = MatchProject(projects, [".Domain"]);
        var application = MatchProject(projects, [".ApplicationService", ".Application", ".App"]);
        var infrastructure = MatchProject(projects, [".Infrastructure", ".Infra", ".Persistence"]);
        var api = MatchProject(projects, [".API", ".Api", ".Web", ".WebApi", ".Presentation"]);

        AssignProject(config, root, inferences, "domainProject", domain,
            (c, rel, ns) => { c.DomainProject = rel; c.DomainNamespace = ns; });
        AssignProject(config, root, inferences, "applicationProject", application,
            (c, rel, ns) => { c.ApplicationProject = rel; c.ApplicationNamespace = ns; });
        AssignProject(config, root, inferences, "infrastructureProject", infrastructure,
            (c, rel, ns) => { c.InfrastructureProject = rel; c.InfrastructureNamespace = ns; });
        AssignProject(config, root, inferences, "apiProject", api,
            (c, rel, ns) => { c.ApiProject = rel; c.ApiNamespace = ns; });

        // ---- IUnitOfWork / UnitOfWork, located by type not by folder guess
        var uowInterface = index.FindType("IUnitOfWork", SyntaxKind.InterfaceDeclaration);
        if (uowInterface is not null && application is not null)
        {
            config.ApplicationUnitOfWorkInterfacePath = RelativeTo(Path.GetDirectoryName(application)!, uowInterface.FilePath);
            inferences.Add(new Inference("applicationUnitOfWorkInterfacePath",
                config.ApplicationUnitOfWorkInterfacePath, Confidence.High, "interface IUnitOfWork"));
        }
        else
        {
            inferences.Add(new Inference("applicationUnitOfWorkInterfacePath",
                config.ApplicationUnitOfWorkInterfacePath, Confidence.Low, "not found — using default"));
        }

        var uowClass = index.FindType("UnitOfWork", SyntaxKind.ClassDeclaration);
        if (uowClass is not null && infrastructure is not null)
        {
            config.InfrastructureUnitOfWorkImplPath = RelativeTo(Path.GetDirectoryName(infrastructure)!, uowClass.FilePath);
            inferences.Add(new Inference("infrastructureUnitOfWorkImplPath",
                config.InfrastructureUnitOfWorkImplPath, Confidence.High, "class UnitOfWork"));
        }
        else
        {
            inferences.Add(new Inference("infrastructureUnitOfWorkImplPath",
                config.InfrastructureUnitOfWorkImplPath, Confidence.Low, "not found — using default"));
        }

        // ---- repository folders, from where existing repositories actually live
        InferFolder(config, inferences, "applicationRepoPath", application,
            index.Interfaces(n => n.EndsWith("Repository", StringComparison.Ordinal) && n != "IUnitOfWork"),
            "I*Repository interfaces",
            (c, v) => c.ApplicationRepoPath = v);

        InferFolder(config, inferences, "infrastructureRepoPath", infrastructure,
            index.Classes(n => n.EndsWith("Repository", StringComparison.Ordinal)),
            "*Repository classes",
            (c, v) => c.InfrastructureRepoPath = v);

        InferFolder(config, inferences, "applicationErrorsPath", application,
            index.Types.Where(t => t.Name == "Errors"),
            "Errors partial class",
            (c, v) => c.ApplicationErrorsPath = v);

        // ---- domain entities folder
        if (domain is not null)
        {
            var entitiesDir = Fs.Directories(Path.GetDirectoryName(domain)!, "Entities")
                .FirstOrDefault();

            if (entitiesDir is not null)
            {
                config.DomainEntitiesPath = RelativeTo(Path.GetDirectoryName(domain)!, entitiesDir);
                inferences.Add(new Inference("domainEntitiesPath", config.DomainEntitiesPath,
                    Confidence.High, "Entities/ directory"));
            }
            else
            {
                inferences.Add(new Inference("domainEntitiesPath", config.DomainEntitiesPath,
                    Confidence.Low, "no Entities/ directory — using default"));
            }
        }

        // ---- role guard style, from which marker interface actually exists
        var singleArray = index.FindType("IRequireExplicitRoles", SyntaxKind.InterfaceDeclaration);
        var roleAndSub = index.FindType("IRequiresExplicitRoles", SyntaxKind.InterfaceDeclaration);

        if (singleArray is not null)
        {
            config.RoleGuardStyle = "single-array";
            inferences.Add(new Inference("roleGuardStyle", "single-array", Confidence.High, "interface IRequireExplicitRoles"));
        }
        else if (roleAndSub is not null)
        {
            config.RoleGuardStyle = "role-and-subrole";
            inferences.Add(new Inference("roleGuardStyle", "role-and-subrole", Confidence.High, "interface IRequiresExplicitRoles"));
        }
        else
        {
            inferences.Add(new Inference("roleGuardStyle", config.RoleGuardStyle, Confidence.Low,
                "neither marker interface found — using default"));
        }

        // ---- role enum
        var roleEnum = index.Types.FirstOrDefault(t =>
            t.Kind == SyntaxKind.EnumDeclaration && t.Name.EndsWith("Role", StringComparison.Ordinal));

        if (roleEnum is not null)
        {
            config.RoleEnum = roleEnum.Name;
            inferences.Add(new Inference("roleEnum", roleEnum.Name, Confidence.High, $"enum {roleEnum.Name}"));
        }
        else
        {
            inferences.Add(new Inference("roleEnum", config.RoleEnum, Confidence.Low, "no *Role enum found — using default"));
        }

        // ---- DbContext
        var dbContext = index.FirstDerivedFrom("DbContext");
        if (dbContext is not null)
        {
            config.DbContextName = dbContext.Name;
            inferences.Add(new Inference("dbContextName", dbContext.Name, Confidence.High, $"class {dbContext.Name} : DbContext"));
        }
        else
        {
            inferences.Add(new Inference("dbContextName", config.ResolvedDbContextName, Confidence.Low,
                "no DbContext subclass found — derived from solutionName"));
        }

        // ---- scheduler, from PackageReferences
        var packages = string.Join(" ", projects.Select(SafeRead));
        (config.Scheduler, var schedulerEvidence, var schedulerConfidence) = packages switch
        {
            _ when packages.Contains("WolverineFx", StringComparison.OrdinalIgnoreCase)
                => ("wolverine", "WolverineFx package reference", Confidence.High),
            _ when packages.Contains("Hangfire", StringComparison.OrdinalIgnoreCase)
                => ("hangfire", "Hangfire package reference", Confidence.High),
            _ when packages.Contains("Quartz", StringComparison.OrdinalIgnoreCase)
                => ("quartz", "Quartz package reference", Confidence.High),
            _ => ("wolverine", "no scheduler package found — using default", Confidence.Low)
        };
        inferences.Add(new Inference("scheduler", config.Scheduler, schedulerConfidence, schedulerEvidence));

        // ---- features folder
        if (application is not null)
        {
            var featuresDir = Fs.Directories(Path.GetDirectoryName(application)!, "Features")
                .FirstOrDefault();

            if (featuresDir is not null)
            {
                config.ApplicationFeaturesPath = RelativeTo(Path.GetDirectoryName(application)!, featuresDir);
                inferences.Add(new Inference("applicationFeaturesPath", config.ApplicationFeaturesPath,
                    Confidence.High, "Features/ directory"));
            }
        }

        return new InferenceResult(config, inferences, root);
    }

    private static void AssignProject(
        ForgeConfig config,
        string root,
        List<Inference> inferences,
        string field,
        string? projectPath,
        Action<ForgeConfig, string, string> assign)
    {
        if (projectPath is null)
        {
            inferences.Add(new Inference(field, "(not found)", Confidence.Low,
                "no project matched the expected name suffix"));
            return;
        }

        var relative = RelativeTo(root, Path.GetDirectoryName(projectPath)!);
        var ns = RootNamespaceOf(projectPath);
        assign(config, relative, ns);

        inferences.Add(new Inference(field, relative, Confidence.High, Path.GetFileName(projectPath)));
    }

    private static void InferFolder(
        ForgeConfig config,
        List<Inference> inferences,
        string field,
        string? projectPath,
        IEnumerable<TypeEntry> candidates,
        string evidence,
        Action<ForgeConfig, string> assign)
    {
        if (projectPath is null) return;

        var projectDir = Path.GetDirectoryName(projectPath)!;

        // Most common directory among matching types — one stray file shouldn't decide.
        var directory = candidates
            .Select(t => Path.GetDirectoryName(t.FilePath)!)
            .Where(d => d.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        if (directory is null) return;

        var relative = RelativeTo(projectDir, directory);
        assign(config, relative);
        inferences.Add(new Inference(field, relative, Confidence.High, evidence));
    }

    private static string? MatchProject(List<string> projects, string[] suffixes) =>
        projects.FirstOrDefault(p => suffixes.Any(s =>
            Path.GetFileNameWithoutExtension(p).EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    private static string RootNamespaceOf(string csprojPath)
    {
        var text = SafeRead(csprojPath);
        const string open = "<RootNamespace>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += open.Length;
            var end = text.IndexOf("</RootNamespace>", start, StringComparison.OrdinalIgnoreCase);
            if (end > start) return text[start..end].Trim();
        }
        return Path.GetFileNameWithoutExtension(csprojPath);
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return string.Empty; }
    }

    /// <summary>Config paths are always relative with forward slashes, on every platform.</summary>
    private static string RelativeTo(string baseDir, string target) =>
        Path.GetRelativePath(baseDir, target).Replace('\\', '/');

}
