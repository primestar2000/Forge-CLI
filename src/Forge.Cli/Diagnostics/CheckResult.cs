namespace Forge.Cli.Diagnostics;

public enum CheckStatus
{
    /// <summary>Healthy.</summary>
    Pass,

    /// <summary>An optional capability is unavailable. Never fails the run — it gates a tier.</summary>
    Warn,

    /// <summary>Something forge needs is wrong. Generators will fail until it is fixed.</summary>
    Fail,

    /// <summary>Could not be evaluated (usually because config is missing).</summary>
    Skip
}

/// <summary>
/// One diagnostic. <see cref="Fix"/> is the command or action that resolves it — doctor exists to
/// tell a user what to do, not merely that something is wrong.
/// </summary>
public sealed record CheckResult(string Label, CheckStatus Status, string Detail, string? Fix = null)
{
    public static CheckResult Pass(string label, string detail) => new(label, CheckStatus.Pass, detail);
    public static CheckResult Warn(string label, string detail, string? fix = null) => new(label, CheckStatus.Warn, detail, fix);
    public static CheckResult Fail(string label, string detail, string? fix = null) => new(label, CheckStatus.Fail, detail, fix);
    public static CheckResult Skip(string label, string detail) => new(label, CheckStatus.Skip, detail);
}

/// <summary>
/// Which command families are usable right now. doctor reports the tier rather than hard-failing,
/// because Tier 0 — all of make:*, stub:*, config:* — needs nothing but the SDK and must work on
/// a legacy solution with no csproj changes at all.
/// </summary>
public enum Tier
{
    None = -1,
    Scaffolding = 0,
    Migrations = 1,
    Runtime = 2
}
