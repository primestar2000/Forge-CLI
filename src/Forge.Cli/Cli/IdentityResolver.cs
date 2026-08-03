using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Cli.Cli;

public sealed record IdentityResult(string? Json, string? Error)
{
    public bool Ok => Error is null;
    public static IdentityResult None { get; } = new(null, null);
}

/// <summary>
/// Resolves the identity <c>invoke:run</c> should sign in as.
///
/// A payload rather than a flag, because handlers want more than a role — id, email, tenant,
/// sub-roles. Named identities live in .forge/identities/, matching the .forge/stubs and
/// .forge/fixtures convention, so a team commits them once and everyone invokes as the same
/// user instead of inventing ad-hoc ids.
/// </summary>
public static class IdentityResolver
{
    public const string Directory = ".forge/identities";

    /// <summary>
    /// --as wins over --as-role: it is the more specific request, and silently preferring the
    /// sugar would discard a file the user explicitly named.
    /// </summary>
    public static IdentityResult Resolve(string solutionRoot, string? name, string? role)
    {
        if (!string.IsNullOrWhiteSpace(name)) return FromFile(solutionRoot, name!);
        if (!string.IsNullOrWhiteSpace(role)) return FromRole(role!);
        return IdentityResult.None;
    }

    /// <summary>Sugar: --as-role Admin becomes a minimal identity.</summary>
    private static IdentityResult FromRole(string role) =>
        new(new JsonObject
        {
            ["id"] = Guid.Empty.ToString(),
            ["email"] = $"{role.ToLowerInvariant()}@forge.local",
            ["role"] = role
        }.ToJsonString(), null);

    private static IdentityResult FromFile(string solutionRoot, string name)
    {
        var path = Path.Combine(solutionRoot, Directory.Replace('/', Path.DirectorySeparatorChar), name + ".json");

        if (!File.Exists(path))
        {
            return new IdentityResult(null,
                $"No identity named '{name}'." + Environment.NewLine +
                $"  -> Expected {Directory}/{name}.json" + Environment.NewLine +
                $"  -> Available: {Available(solutionRoot)}");
        }

        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception ex) { return new IdentityResult(null, $"Could not read {path}: {ex.Message}"); }

        // Validate here rather than in the child process: a JSON typo should fail in
        // milliseconds, not after a full build and host startup.
        try
        {
            var parsed = JsonNode.Parse(content)?.AsObject()
                ?? throw new JsonException("not a JSON object");

            if (parsed["role"] is null)
            {
                return new IdentityResult(null,
                    $"{Directory}/{name}.json has no \"role\"." + Environment.NewLine +
                    "  -> The role guard needs one; add e.g. \"role\": \"Admin\".");
            }

            return new IdentityResult(parsed.ToJsonString(), null);
        }
        catch (JsonException ex)
        {
            return new IdentityResult(null, $"{Directory}/{name}.json is not valid JSON: {ex.Message}");
        }
    }

    private static string Available(string solutionRoot)
    {
        var directory = Path.Combine(solutionRoot, Directory.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.Directory.Exists(directory)) return "(none - create the directory to add some)";

        var names = System.IO.Directory.GetFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
