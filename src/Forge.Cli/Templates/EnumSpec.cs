namespace Forge.Cli.Templates;

/// <summary>One enum member. <paramref name="Value"/> is null when the member is unnumbered.</summary>
public sealed record EnumMemberSpec(string Name, long? Value);

/// <summary>What make:enum was asked to build.</summary>
public sealed record EnumSpec(string Name, IReadOnlyList<EnumMemberSpec> Members, bool IsFlags = false);

public sealed record EnumParseResult(IReadOnlyList<EnumMemberSpec> Members, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Parses --values into enum members.
///
///   "Pending,Paid,Shipped"        unnumbered
///   "Pending=1,Paid=2"            explicit
///   "None=0,Read=1,Write=2"       explicit, with --flags
///
/// Explicit values matter more here than they look: enum members are persisted by EF Core as
/// integers, so reordering an unnumbered enum silently reinterprets every row already in the
/// database. Numbering is how that is prevented.
/// </summary>
public static class EnumValueParser
{
    public static EnumParseResult Parse(string? raw, bool isFlags = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new EnumParseResult([], "No values given. Use --values \"Pending,Paid,Shipped\".");

        var members = new List<EnumMemberSpec>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            var name = NameHelper.Pascal(parts[0]);

            if (!NameHelper.IsValidIdentifier(name))
                return new EnumParseResult([], $"'{parts[0]}' is not a valid C# identifier, so it cannot name an enum member.");

            if (!seenNames.Add(name))
                return new EnumParseResult([], $"'{name}' appears twice - enum members must be unique.");

            long? value = null;
            if (parts.Length == 2)
            {
                if (!long.TryParse(parts[1], out var parsed))
                    return new EnumParseResult([], $"'{parts[1]}' is not a whole number, so it cannot be the value of '{name}'.");

                value = parsed;
            }

            members.Add(new EnumMemberSpec(name, value));
        }

        if (members.Count == 0)
            return new EnumParseResult([], "No values given. Use --values \"Pending,Paid,Shipped\".");

        // A [Flags] enum whose members are 0,1,2,3,4 is the classic silent bug: 3 is not a
        // distinct flag, it is Read|Write, and combinations start overlapping. Number them
        // as powers of two unless the caller said otherwise.
        if (isFlags && members.All(m => m.Value is null))
        {
            // Only a member actually named None gets 0. Keying off position instead would give
            // --flags --values "Read,Write" a Read of 0, which means "no flags set" — an enum
            // that silently never matches.
            var next = 1L;
            members = [.. members.Select(m => m.Name == "None"
                ? m with { Value = 0 }
                : m with { Value = Shift(ref next) })];
        }

        return new EnumParseResult(members, null);
    }

    private static long Shift(ref long next)
    {
        var value = next;
        next <<= 1;
        return value;
    }
}
