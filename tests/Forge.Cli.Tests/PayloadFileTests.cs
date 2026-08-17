using System.Diagnostics;

namespace Forge.Cli.Tests;

/// <summary>
/// --payload-file, driven through the real parser.
///
/// Exists because PowerShell and cmd mangle quotes in inline JSON — reported from the field as
/// needing --% or manual escaping on Windows. These cases are the ones that must fail fast:
/// every one of them is cheap to detect here and expensive to discover after a build and a
/// host start inside the user's application.
/// </summary>
public class PayloadFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-pf-" + Guid.NewGuid().ToString("N")[..8]);

    public PayloadFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// These cases need the real parser in a real process, so they launch the built CLI. The
    /// configuration and target framework are taken from this test assembly's own output path
    /// rather than hardcoded: CI builds Release, and a hardcoded Debug path made `dotnet` fail to
    /// find the assembly at all — which exits 1, so three tests asserting exit 2 failed on the
    /// runner while passing locally against a stale Debug build.
    /// </summary>
    private static readonly string CliDll = ResolveCliDll();

    private static string ResolveCliDll()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = output.Name;                       // net8.0
        var configuration = output.Parent?.Name;           // Debug | Release

        var directory = output;
        while (directory is not null)
        {
            if (directory.GetFiles("*.slnx").Length > 0 || directory.GetFiles("*.sln").Length > 0)
                break;
            directory = directory.Parent;
        }

        if (directory is null)
            throw new InvalidOperationException(
                "Could not locate the repository root from " + AppContext.BaseDirectory);

        var candidate = configuration is null
            ? null
            : Path.Combine(directory.FullName, "src", "Forge.Cli", "bin", configuration, framework, "Forge.Cli.dll");

        if (candidate is not null && File.Exists(candidate)) return candidate;

        throw new InvalidOperationException(
            $"Forge.Cli.dll was not found at '{candidate}'. Build the CLI in the '{configuration}' " +
            "configuration before running these tests.");
    }

    private (int ExitCode, string Output) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        info.ArgumentList.Add(CliDll);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, output);
    }

    private string WritePayload(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void A_missing_payload_file_names_the_resolved_path()
    {
        var result = Run("invoke:run", "SomeCommand", "--payload-file", "nope.json");

        Assert.Equal(Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Payload file not found", result.Output);
        Assert.Contains("nope.json", result.Output);
    }

    /// <summary>Milliseconds here, versus a build plus a host start to find out otherwise.</summary>
    [Fact]
    public void Malformed_json_fails_before_anything_is_launched()
    {
        var path = WritePayload("bad.json", "{ this is not json");

        var result = Run("invoke:run", "SomeCommand", "--payload-file", path);

        Assert.Equal(Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("not valid JSON", result.Output);
    }

    /// <summary>
    /// Both set the same thing, so silently preferring one would make the other look ignored.
    /// </summary>
    [Fact]
    public void Payload_and_payload_file_together_are_rejected()
    {
        var path = WritePayload("good.json", """{"Reference":"x"}""");

        var result = Run("invoke:run", "SomeCommand",
            "--payload", """{"Reference":"y"}""", "--payload-file", path);

        Assert.Equal(Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("both given", result.Output);
    }

    /// <summary>
    /// A valid file must get past parsing. It then fails on config, because this temp directory
    /// is not a forge solution — which is exactly how far this test should reach.
    /// </summary>
    [Fact]
    public void A_valid_payload_file_passes_validation()
    {
        var path = WritePayload("good.json", """{"Reference":"forge-check","Total":42}""");

        var result = Run("invoke:run", "SomeCommand", "--payload-file", path);

        Assert.NotEqual(Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.DoesNotContain("not valid JSON", result.Output);
        Assert.DoesNotContain("Payload file not found", result.Output);
    }
}
