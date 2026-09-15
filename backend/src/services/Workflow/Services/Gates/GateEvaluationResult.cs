namespace Custodian.Workflow.Services.Gates;

/// <summary>
/// Outcome of a single named requirement within a gate check (e.g. one required document type).
/// </summary>
public sealed record GateRequirementResult(string RequirementName, bool IsSatisfied, string? Reason);

/// <summary>
/// Outcome of evaluating a mandatory gate for a stage transition (CSTD-18). A blocked
/// result always carries a human-readable Reason so the caller (the controller, and
/// ultimately the frontend's error banner) can surface why the transition was rejected.
/// </summary>
public sealed record GateEvaluationResult(bool IsSatisfied, string? Reason, IReadOnlyList<GateRequirementResult> Requirements)
{
    public static GateEvaluationResult Satisfied(IReadOnlyList<GateRequirementResult>? requirements = null) =>
        new(true, null, requirements ?? Array.Empty<GateRequirementResult>());

    public static GateEvaluationResult Blocked(string reason, IReadOnlyList<GateRequirementResult> requirements) =>
        new(false, reason, requirements);
}
