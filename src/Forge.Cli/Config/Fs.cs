namespace Forge.Cli.Config;

/// <summary>
/// Filesystem helpers with the safety properties forge needs when walking a user's machine.
/// </summary>
public static class Fs
{
    /// <summary>
    /// Recursive enumeration that survives real filesystems.
    ///
    /// IgnoreInaccessible skips permission-denied directories instead of throwing — Windows user
    /// profiles contain legacy junctions like "Application Data" that deny access to everyone.
    /// Skipping ReparsePoint also prevents following junctions/symlinks into cycles.
    /// </summary>
    public static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        MatchType = MatchType.Simple
    };

    public static IEnumerable<string> Files(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, pattern, Recursive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static IEnumerable<string> Directories(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateDirectories(root, pattern, Recursive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Walks upward looking for a marker, BOUNDED so it cannot escape the project.
    ///
    /// Without bounds, running forge in a temp directory finds a stray forge.config.json in the
    /// user's home folder and happily treats the home directory as a solution root — scaffolding
    /// source files into it. Observed in testing; hence the two stop conditions:
    ///   - stop after checking a directory containing .git (the repository boundary)
    ///   - never check the user profile directory or anything above it
    /// </summary>
    public static string? FindUpward(string startDirectory, Func<DirectoryInfo, string?> probe)
    {
        var home = SafeHome();
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            if (home is not null && PathsEqual(directory.FullName, home)) return null;

            var hit = probe(directory);
            if (hit is not null) return hit;

            // Repository boundary — a config outside this repo is not ours.
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return null;

            directory = directory.Parent;
        }

        return null;
    }

    public static string? FindFileUpward(string startDirectory, string fileName) =>
        FindUpward(startDirectory, dir =>
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            return File.Exists(candidate) ? candidate : null;
        });

    public static string? FindByPatternUpward(string startDirectory, string pattern) =>
        FindUpward(startDirectory, dir =>
        {
            try { return dir.GetFiles(pattern).FirstOrDefault()?.FullName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        });

    private static string? SafeHome()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) ? null : Path.GetFullPath(home);
        }
        catch { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
