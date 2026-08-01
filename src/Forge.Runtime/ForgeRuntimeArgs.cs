namespace Forge.Runtime;

/// <summary>
/// Reads the <c>--forge-&lt;name&gt; &lt;value&gt;</c> options forge passes alongside the verb.
///
/// Public because satellite packages parse the same arguments, and duplicating the convention in
/// each one is how the two halves quietly drift apart.
/// </summary>
public static class ForgeRuntimeArgs
{
    public const string OptionPrefix = "--forge-";

    /// <summary>Value following <c>--forge-{name}</c>, or null when absent.</summary>
    public static string? Option(string[] args, string name)
    {
        var flag = OptionPrefix + name;

        // Stop one short: a trailing flag with no value must not read past the end.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal)) return args[i + 1];
        }

        return null;
    }

    public static bool Flag(string[] args, string name) =>
        args.Contains(OptionPrefix + name, StringComparer.Ordinal);
}
