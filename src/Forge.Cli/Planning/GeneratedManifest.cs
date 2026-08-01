using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Cli.Planning;

public sealed record ManifestEntry(string Hash, string GeneratedBy, string GeneratedUtc);

/// <summary>
/// Records the content hash of every file forge has fully generated, so <c>--force</c> can tell
/// untouched boilerplate from a file a human has since edited.
///
/// Without this, <c>--force</c> is "hope you committed first": it overwrites a handler someone
/// spent a day on exactly as readily as a file nobody has opened. With it, re-running a generator
/// over unmodified output is safe and routine, while overwriting edited work needs a second,
/// explicit flag.
///
/// Only <see cref="FileAction.Create"/> targets are tracked. Patched files (IUnitOfWork,
/// DbContext, DependencyInjection) are co-owned with the developer by design, and patches are
/// additive and idempotent, so they are never overwritten wholesale.
/// </summary>
public sealed class GeneratedManifest
{
    public const string FileName = ".forge/manifest.json";
    private const int CurrentVersion = 1;

    private readonly Dictionary<string, ManifestEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string SolutionRoot { get; }
    public string Path { get; }

    private GeneratedManifest(string solutionRoot)
    {
        SolutionRoot = solutionRoot;
        Path = System.IO.Path.Combine(solutionRoot, FileName.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public static GeneratedManifest Load(string solutionRoot)
    {
        var manifest = new GeneratedManifest(solutionRoot);
        if (!File.Exists(manifest.Path)) return manifest;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(manifest.Path))?.AsObject();
            if (root?["files"]?.AsObject() is not { } files) return manifest;

            foreach (var (key, value) in files)
            {
                if (value is not JsonObject entry) continue;
                manifest._entries[key] = new ManifestEntry(
                    entry["hash"]?.GetValue<string>() ?? string.Empty,
                    entry["generatedBy"]?.GetValue<string>() ?? string.Empty,
                    entry["generatedUtc"]?.GetValue<string>() ?? string.Empty);
            }
        }
        catch
        {
            // A corrupt manifest must never block generation; it only downgrades --force to
            // treating everything as potentially modified, which is the safe direction.
        }

        return manifest;
    }

    /// <summary>SHA-256 over line-ending-normalised content, so CRLF/LF churn is not "modified".</summary>
    public static string ComputeHash(string content)
    {
        var normalised = content.Replace("\r\n", "\n");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
    }

    public string RelativeKey(string absolutePath) =>
        System.IO.Path.GetRelativePath(SolutionRoot, absolutePath).Replace('\\', '/');

    public ManifestEntry? Find(string absolutePath) =>
        _entries.TryGetValue(RelativeKey(absolutePath), out var entry) ? entry : null;

    /// <summary>
    /// True when the file on disk differs from what forge generated — including the case where
    /// forge has no record of it at all. Unknown provenance is treated as modified: refusing to
    /// clobber a file forge cannot prove it wrote is the safe default.
    /// </summary>
    public bool IsModifiedSinceGeneration(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return false;

        var entry = Find(absolutePath);
        if (entry is null) return true;

        try
        {
            return ComputeHash(PlanExecutor.ReadPreservingEncoding(absolutePath, out _)) != entry.Hash;
        }
        catch
        {
            return true;
        }
    }

    public void Record(string absolutePath, string content, string generatedBy) =>
        _entries[RelativeKey(absolutePath)] = new ManifestEntry(
            ComputeHash(content), generatedBy, DateTime.UtcNow.ToString("O"));

    public bool IsEmpty => _entries.Count == 0;

    public string Serialise()
    {
        var files = new JsonObject();
        foreach (var (key, entry) in _entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            files[key] = new JsonObject
            {
                ["hash"] = entry.Hash,
                ["generatedBy"] = entry.GeneratedBy,
                ["generatedUtc"] = entry.GeneratedUtc
            };
        }

        return new JsonObject
        {
            ["version"] = CurrentVersion,
            ["files"] = files
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }
}
