using System.Diagnostics;
using System.Text.Json.Nodes;
using Forge.Cli.Config;

namespace Forge.Cli.Cli;

public sealed record RuntimeResult(bool Ok, JsonObject? Payload, string? Error, string RawOutput)
{
    public JsonNode? Data => Payload?["data"];
}

/// <summary>
/// Runs a forge verb inside the USER's application, out of process.
///
/// forge cannot load the user's assemblies into itself: it already holds Roslyn and its own
/// dependency graph, their app targets its own TFM with its own transitive versions, and
/// Assembly.LoadFrom would not consult their deps.json. So forge launches their app with a
/// --forge:&lt;verb&gt; sentinel, Forge.Runtime intercepts it inside the real DI container, and the
/// answer comes back as versioned JSON. Same trade dotnet-ef makes, for the same reasons.
/// </summary>
public static class RuntimeBridge
{
    public const string PackageId = "Pitechy.Forge.Runtime";

    private const string BeginMarker = "<<<FORGE-RESULT>>>";
    private const string EndMarker = "<<<END-FORGE-RESULT>>>";

    /// <summary>Cheap pre-flight so a missing package is a clear message, not a confusing failure.</summary>
    public static bool IsReferenced(ForgeConfig config, string solutionRoot)
    {
        var project = Path.Combine(solutionRoot, config.ApiProject);
        if (!Directory.Exists(project)) return false;

        return Directory
            .EnumerateFiles(project, "*.csproj", SearchOption.TopDirectoryOnly)
            .Any(file =>
            {
                try { return File.ReadAllText(file).Contains(PackageId, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
    }

    public static RuntimeResult Invoke(
        ForgeConfig config,
        string solutionRoot,
        string verb,
        IReadOnlyList<string> extraArguments,
        bool noBuild,
        Output output,
        bool verbose)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = solutionRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(config.ApiProject);
        if (noBuild) info.ArgumentList.Add("--no-build");

        // Everything after -- belongs to the app, not to `dotnet run`.
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(ForgeRuntimeArgument(verb));
        foreach (var argument in extraArguments) info.ArgumentList.Add(argument);

        // Default the environment only when the caller has not set one — their value must win.
        // Note ??= is wrong here: the IDictionary indexer THROWS on a missing key rather than
        // returning null, so reading it to test for absence is itself the bug.
        if (!info.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT"))
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        if (verbose) output.Dim("  dotnet " + string.Join(" ", info.ArgumentList));

        string combined;
        int exitCode;

        try
        {
            using var process = Process.Start(info);
            if (process is null) return new RuntimeResult(false, null, "Could not start 'dotnet'.", string.Empty);

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(600_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new RuntimeResult(false, null, "The application did not exit within 10 minutes.", stdout + stderr);
            }

            combined = stdout + stderr;
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            return new RuntimeResult(false, null, ex.Message, string.Empty);
        }

        var json = Extract(combined);
        if (json is null)
        {
            // No envelope means the app never reached the hook. Almost always a wiring problem,
            // so say exactly what to add rather than dumping the child process's output.
            var reason = exitCode == 0
                ? $"The application ran but produced no forge result." + Environment.NewLine +
                  $"  -> Add this to {config.ApiProject}/Program.cs, before app.Run():" + Environment.NewLine +
                  $"       if (await app.RunForgeRuntimeAsync(args)) return;"
                : $"The application exited with code {exitCode} before producing a forge result.";

            return new RuntimeResult(false, null, reason, combined);
        }

        try
        {
            var payload = JsonNode.Parse(json)?.AsObject();
            if (payload is null) return new RuntimeResult(false, null, "Malformed forge result.", combined);

            var success = payload["success"]?.GetValue<bool>() ?? false;
            var error = payload["error"]?.GetValue<string>();

            return new RuntimeResult(success, payload, success ? null : error, combined);
        }
        catch (Exception ex)
        {
            return new RuntimeResult(false, null, $"Could not parse the forge result: {ex.Message}", combined);
        }
    }

    internal static string ForgeRuntimeArgument(string verb) => "--forge:" + verb;

    /// <summary>
    /// Pulls the payload out of stdout, which also carries the application's own logging —
    /// startup banners, EF SQL, whatever the user's logger emits.
    /// </summary>
    internal static string? Extract(string output)
    {
        var start = output.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return null;

        start += BeginMarker.Length;

        var end = output.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return null;

        var json = output[start..end].Trim();
        return json.Length == 0 ? null : json;
    }
}
