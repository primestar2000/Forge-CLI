using System.Text;
using Forge.Cli.Cli;

namespace Forge.Cli.Planning;

public sealed record ExecutionResult(int ExitCode, int Created, int Patched, int Skipped, string? Error = null)
{
    public bool Ok => ExitCode == ExitCodes.Success;
}

/// <summary>
/// The ONLY type in the codebase permitted to write to disk.
///
/// Applies a plan all-or-nothing: every file is staged to a sibling temp file first, and the
/// moves only happen once every staged write has succeeded. A failure part-way through leaves
/// the working tree exactly as it was — there is never a half-scaffolded feature to hand-fix.
/// </summary>
public sealed class PlanExecutor
{
    private const string TempSuffix = ".forge-tmp";

    public ExecutionResult Apply(GenerationPlan plan)
    {
        var conflicts = plan.ConflictingPaths;
        if (conflicts.Count > 0)
        {
            return new ExecutionResult(ExitCodes.Error, 0, 0, 0,
                "Two actions target the same file, which would make the result order-dependent:" +
                Environment.NewLine + string.Join(Environment.NewLine, conflicts.Select(p => "  " + p)));
        }

        var staged = new List<(string Temp, string Final)>();
        try
        {
            foreach (var action in plan.Actions)
            {
                switch (action)
                {
                    case FileAction.Create create:
                        staged.Add(Stage(create.Path, create.Content));
                        break;

                    case FileAction.Patch patch:
                        // Re-read and compare: if the file changed on disk since planning, the
                        // computed "after" text is stale and applying it would silently discard
                        // whatever changed.
                        var current = ReadPreservingEncoding(patch.Path, out var encoding);
                        if (!string.Equals(current, patch.Before, StringComparison.Ordinal))
                        {
                            return Rollback(staged, ExitCodes.Error,
                                $"{patch.Path} changed on disk after the plan was computed. " +
                                "Nothing was written. Re-run the command.");
                        }
                        staged.Add(Stage(patch.Path, patch.After, encoding));
                        break;

                    case FileAction.Skip:
                        break;
                }
            }

            // Every staged write succeeded — commit.
            foreach (var (temp, final) in staged)
            {
                var dir = Path.GetDirectoryName(final);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Move(temp, final, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return Rollback(staged, ExitCodes.Error, ex.Message);
        }

        return new ExecutionResult(
            ExitCodes.Success,
            plan.Actions.Count(a => a is FileAction.Create),
            plan.Actions.Count(a => a is FileAction.Patch),
            plan.Actions.Count(a => a is FileAction.Skip));
    }

    private static (string Temp, string Final) Stage(string path, string content, Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + TempSuffix;
        // Default to UTF-8 without BOM for new files; patched files keep whatever they had.
        File.WriteAllText(temp, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (temp, path);
    }

    private static ExecutionResult Rollback(List<(string Temp, string Final)> staged, int code, string error)
    {
        foreach (var (temp, _) in staged)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort — a leftover .forge-tmp is noise, not damage */ }
        }
        return new ExecutionResult(code, 0, 0, 0, error);
    }

    /// <summary>
    /// Reads a file and reports the encoding it was stored in, so a patch doesn't silently
    /// strip a BOM — which git would render as a whole-file diff.
    /// </summary>
    internal static string ReadPreservingEncoding(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }

        encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new UTF8Encoding(false).GetString(bytes);
    }
}
