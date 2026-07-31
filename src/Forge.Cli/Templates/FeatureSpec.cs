namespace Forge.Cli.Templates;

public enum MessageKind { Command, Query }

public sealed record FeatureSpec(
    string Name,
    MessageKind Kind,
    IReadOnlyList<PropertySpec> Properties,
    IReadOnlyList<string> Roles,
    bool Anonymous,
    string? Group,
    string? Returns,
    bool WithRepo,
    string? Entity)
{
    /// <summary>CreateGig + Command -> CreateGigCommand. Avoids CreateGigCommandCommand.</summary>
    public string MessageName
    {
        get
        {
            var suffix = Kind == MessageKind.Command ? "Command" : "Query";
            var name = NameHelper.Pascal(Name);
            return name.EndsWith(suffix, StringComparison.Ordinal) ? name : name + suffix;
        }
    }

    public string FolderSegment => Kind == MessageKind.Command ? "Commands" : "Queries";
}
