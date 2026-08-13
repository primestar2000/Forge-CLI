using System.Text;
using Forge.Cli.Planning;

namespace Forge.Cli.Templates.OnionWolverineErrorOr;

/// <summary>
/// make:enum — a Domain enum.
///
/// Deliberately the whole command: no DbContext patch, no configuration file. EF Core persists
/// an enum as its underlying integer with no mapping, so there is nothing to wire. Generating a
/// HasConversion call nobody asked for would be noise in a file the developer has to read.
/// </summary>
internal static class EnumScaffold
{
    public static PlanResult Plan(TemplateContext ctx, EnumSpec spec)
    {
        var name = NameHelper.Pascal(spec.Name);

        if (!NameHelper.IsValidIdentifier(name))
            return PlanResult.UsageError($"'{spec.Name}' is not a valid C# identifier, so it cannot name an enum.");

        if (spec.Members.Count == 0)
            return PlanResult.UsageError($"'{name}' has no values. Use --values \"Pending,Paid,Shipped\".");

        var config = ctx.Config;
        var enumsNamespace = Namespaces.For(config.DomainNamespace, config.DomainEnumsPath);
        var path = ctx.PathIn(config.DomainProject, config.DomainEnumsPath, $"{name}.cs");

        var plan = ctx.CreateOrSkip(path, () => ctx.Render("Enum.cs.txt", new Dictionary<string, string>
        {
            // [Flags] lives in System, and the generated file may be the only thing in the
            // project that needs it.
            ["Usings"] = spec.IsFlags ? ctx.Usings("System") : string.Empty,
            ["Namespace"] = enumsNamespace,
            ["Attribute"] = spec.IsFlags ? "[Flags]" + ctx.CodeStyle.NewLine : string.Empty,
            ["Enum"] = name,
            ["Members"] = RenderMembers(spec.Members, ctx.CodeStyle.IndentUnit)
        }));

        return PlanResult.Success(plan);
    }

    private static string RenderMembers(IReadOnlyList<EnumMemberSpec> members, string indent)
    {
        var sb = new StringBuilder();

        foreach (var member in members)
        {
            if (sb.Length > 0) sb.Append(',').Append('\n');

            sb.Append(indent).Append(member.Name);
            if (member.Value is { } value) sb.Append(" = ").Append(value);
        }

        return sb.ToString();
    }
}
