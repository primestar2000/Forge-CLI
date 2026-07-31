namespace Forge.Cli.Cli;

/// <summary>
/// Console output. The only layer allowed to write to stdout/stderr — generators return
/// plans and reasons, never text.
///
/// Rule: when --json is set, stdout carries JSON and nothing else. Every human-readable
/// message goes to stderr, or the output stops being parseable.
/// </summary>
public sealed class Output(bool json, bool noColor)
{
    // Built from the char code so no literal control character lives in the source file.
    private static readonly string Esc = ((char)27).ToString();
    private static readonly string Reset = Esc + "[0m";
    private static readonly string Green = Esc + "[32m";
    private static readonly string Red = Esc + "[31m";
    private static readonly string Yellow = Esc + "[33m";
    private static readonly string Grey = Esc + "[90m";

    private readonly bool _color = !noColor
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && !Console.IsOutputRedirected;

    public bool JsonMode { get; } = json;

    private TextWriter Human => JsonMode ? Console.Error : Console.Out;

    public void Success(string message) => Write(Human, message, Green);
    public void Failure(string message) => Write(Console.Error, message, Red);
    public void Warn(string message) => Write(Human, message, Yellow);
    public void Dim(string message) => Write(Human, message, Grey);
    public void Info(string message) => Human.WriteLine(message);
    public void Blank() => Human.WriteLine();

    /// <summary>Machine-readable payload. Always stdout, never colored.</summary>
    public void Json(string payload) => Console.Out.WriteLine(payload);

    public void Diff(string rendered)
    {
        foreach (var raw in rendered.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.StartsWith(" + ", StringComparison.Ordinal)) Write(Human, text, Green);
            else if (text.StartsWith(" - ", StringComparison.Ordinal)) Write(Human, text, Red);
            else if (text.Length > 0) Dim(text);
        }
    }

    private void Write(TextWriter writer, string message, string colorCode) =>
        writer.WriteLine(_color ? colorCode + message + Reset : message);
}
