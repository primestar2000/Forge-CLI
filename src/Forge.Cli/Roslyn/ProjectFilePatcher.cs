namespace Forge.Cli.Roslyn;

/// <summary>
/// Adds a PackageReference to a csproj.
///
/// A targeted text insertion rather than an XML round-trip. XDocument would reformat the whole
/// file — collapsing empty elements, normalising attribute quoting and indentation — turning a
/// one-line addition into an unreviewable diff. The "no text manipulation" rule exists to keep
/// C# edits on Roslyn; for a single well-anchored insertion into markup, a minimal diff matters
/// more than parser purity.
///
/// Idempotent: an existing reference to the same id is left alone, whatever its version.
/// </summary>
public static class ProjectFilePatcher
{
    public static PatchOutcome AddPackageReference(
        string source,
        string packageId,
        string version,
        string newLine,
        string indentUnit)
    {
        // Quoted, so "Pitechy.Forge.Runtime" is not considered present because
        // "Pitechy.Forge.Runtime.Wolverine" is.
        if (source.Contains($"\"{packageId}\"", StringComparison.OrdinalIgnoreCase))
            return new PatchOutcome.AlreadyPresent($"{packageId} is already referenced");

        // Preferred anchor: the closing tag of the ItemGroup that already holds package
        // references, so related things stay together.
        var lastPackageReference = source.LastIndexOf("<PackageReference", StringComparison.OrdinalIgnoreCase);
        if (lastPackageReference >= 0)
        {
            var closing = source.IndexOf("</ItemGroup>", lastPackageReference, StringComparison.OrdinalIgnoreCase);
            if (closing >= 0)
            {
                // Copy the sibling's own indentation rather than composing it from the C# indent
                // unit. csproj files conventionally use two spaces, so deriving it from a
                // four-space C# style produces a line that does not line up with its neighbours.
                var sibling = IndentOfLineAt(source, lastPackageReference);

                var lineStart = source.LastIndexOf('\n', closing);
                var insertAt = lineStart < 0 ? closing : lineStart + 1;

                var line = $"{sibling}<PackageReference Include=\"{packageId}\" Version=\"{version}\" />";
                return new PatchOutcome.Patched(source.Insert(insertAt, line + newLine));
            }
        }

        var reference = $"{indentUnit}{indentUnit}<PackageReference Include=\"{packageId}\" Version=\"{version}\" />";

        // Fallback: no package references yet, so add an ItemGroup before </Project>.
        var project = source.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        if (project < 0)
        {
            return new PatchOutcome.Failed(
                "The project file has no </Project> element, so forge cannot add a package reference." +
                Environment.NewLine + $"  -> dotnet add package {packageId} --version {version}");
        }

        var group =
            $"{newLine}{indentUnit}<ItemGroup>{newLine}" +
            $"{reference}{newLine}" +
            $"{indentUnit}</ItemGroup>{newLine}";

        return new PatchOutcome.Patched(source.Insert(project, group));
    }

    /// <summary>Leading whitespace of the line containing <paramref name="index"/>.</summary>
    private static string IndentOfLineAt(string source, int index)
    {
        var start = source.LastIndexOf('\n', index) + 1;

        var end = start;
        while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;

        return source[start..end];
    }
}
