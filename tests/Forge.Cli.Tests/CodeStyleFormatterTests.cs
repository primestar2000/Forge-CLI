using Forge.Cli.Config;
using Forge.Cli.Stubs;

namespace Forge.Cli.Tests;

/// <summary>
/// Which usings a generated file carries.
///
/// This suppressed EVERY using when ImplicitUsings was on, which is right for System and wrong
/// for anything else — ImplicitUsings has never provided the project's own namespaces. The
/// visible symptom was an entity with an enum property that would not compile.
/// </summary>
public class CodeStyleFormatterTests
{
    private static CodeStyle Style(bool implicitUsings) =>
        CodeStyle.Default with { ImplicitUsings = implicitUsings };

    [Fact]
    public void Implicit_usings_suppress_only_what_they_actually_provide()
    {
        var usings = CodeStyleFormatter.UsingsFor(Style(true), "System", "App.Domain.Enums");

        Assert.DoesNotContain("using System;", usings);
        Assert.Contains("using App.Domain.Enums;", usings);
    }

    [Fact]
    public void Nothing_is_emitted_when_every_namespace_is_implicit()
    {
        Assert.Equal(string.Empty,
            CodeStyleFormatter.UsingsFor(Style(true), "System", "System.Linq", "System.Threading.Tasks"));
    }

    [Fact]
    public void Without_implicit_usings_everything_is_emitted()
    {
        var usings = CodeStyleFormatter.UsingsFor(Style(false), "System", "App.Domain.Enums");

        Assert.Contains("using System;", usings);
        Assert.Contains("using App.Domain.Enums;", usings);
    }

    [Fact]
    public void No_namespaces_emits_nothing()
    {
        Assert.Equal(string.Empty, CodeStyleFormatter.UsingsFor(Style(false)));
        Assert.Equal(string.Empty, CodeStyleFormatter.UsingsFor(Style(true)));
    }
}
