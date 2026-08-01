using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Runtime;

/// <summary>
/// The contract between the forge CLI and the user's application.
///
/// Deliberately JSON and never shared types. The two processes have entirely separate dependency
/// graphs, so a shared type would reintroduce exactly the version coupling the process boundary
/// exists to avoid. Version skew between Forge.Cli and Forge.Runtime is therefore a non-issue by
/// construction: they only have to agree on this schema.
///
/// The payload is wrapped in markers because it shares stdout with the application's own logging.
/// A host writes startup banners, EF writes SQL, Serilog writes whatever it likes — forge extracts
/// the text between the markers rather than trying to parse the whole stream.
/// </summary>
public static class ForgeEnvelope
{
    public const string BeginMarker = "<<<FORGE-RESULT>>>";
    public const string EndMarker = "<<<END-FORGE-RESULT>>>";

    public const int SchemaVersion = 1;

    public static string Success(string verb, JsonNode? data) => Write(verb, true, data, null);

    public static string Failure(string verb, string error) => Write(verb, false, null, error);

    private static string Write(string verb, bool success, JsonNode? data, string? error)
    {
        var payload = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["runtimeVersion"] = typeof(ForgeEnvelope).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            ["verb"] = verb,
            ["success"] = success,
            ["data"] = data,
            ["error"] = error
        };

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine(BeginMarker);
        builder.AppendLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        builder.AppendLine(EndMarker);
        return builder.ToString();
    }

    /// <summary>
    /// Pulls the payload out of a stdout stream that also contains application logging.
    /// Returns null when no envelope is present — which means the app never reached the forge
    /// hook, usually because RunForgeRuntimeAsync was not wired into Program.cs.
    /// </summary>
    public static string? Extract(string output)
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
