using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Gates;

public interface IGateEvaluator
{
    /// <summary>
    /// Evaluates whether an engagement may transition into targetStage. Pure with respect
    /// to the engagement's own transition rules (already checked by EngagementStageValidator
    /// before this runs) — this only checks mandatory-gate requirements (required documents
    /// today; Approval/Payment conditions once those exist).
    /// </summary>
    Task<GateEvaluationResult> EvaluateAsync(Guid engagementId, string tenantId, EngagementStage targetStage, CancellationToken ct = default);
}
