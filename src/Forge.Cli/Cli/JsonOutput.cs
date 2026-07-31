using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Cli.Planning;

namespace Forge.Cli.Cli;

/// <summary>
/// The --json contract. CI depends on this shape, so adding a field is fine but removing or
/// renaming one is breaking — hence the explicit schemaVersion.
/// </summary>
public static class JsonOutput
{
    public const int SchemaVersion = 1;

    public static string Render(
        string command,
        GenerationPlan plan,
        int exitCode,
        string solutionRoot,
        string? error = null)
    {
        var actions = new JsonArray();
        foreach (var action in plan.Actions)
        {
            var node = new JsonObject
            {
                ["type"] = action.Kind,
                // Always relative to the solution root with forward slashes, on every platform.
                ["path"] = Path.GetRelativePath(solutionRoot, action.Path).Replace('\\', '/')
            };
            if (action is FileAction.Skip skip) node["reason"] = skip.Reason;
            actions.Add(node);
        }

        var diagnostics = new JsonArray();
        if (!string.IsNullOrEmpty(error)) diagnostics.Add(error);

        var payload = new JsonObject
        {
            ["forgeVersion"] = ForgeVersion.Current,
            ["schemaVersion"] = SchemaVersion,
            ["command"] = command,
            ["success"] = exitCode == ExitCodes.Success,
            ["exitCode"] = exitCode,
            ["actions"] = actions,
            ["diagnostics"] = diagnostics
        };

        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

public static class ForgeVersion
{
    public static string Current =>
        typeof(ForgeVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
