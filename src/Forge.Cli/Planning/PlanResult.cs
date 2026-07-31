using Forge.Cli.Cli;

namespace Forge.Cli.Planning;

/// <summary>
/// Planning can legitimately fail — a missing anchor, a missing configured file — and that
/// failure must carry a specific exit code. Wrapping the plan keeps generators free of any
/// console or exception handling.
/// </summary>
public sealed record PlanResult(GenerationPlan Plan, int ExitCode, string? Error)
{
    public bool Ok => ExitCode == ExitCodes.Success;

    public static PlanResult Success(GenerationPlan plan) => new(plan, ExitCodes.Success, null);

    public static PlanResult Fail(int exitCode, string error) => new(GenerationPlan.Empty, exitCode, error);

    public static PlanResult AnchorNotFound(string error) => Fail(ExitCodes.AnchorNotFound, error);

    public static PlanResult ConfigInvalid(string error) => Fail(ExitCodes.ConfigInvalid, error);

    public static PlanResult UsageError(string error) => Fail(ExitCodes.UsageError, error);
}
