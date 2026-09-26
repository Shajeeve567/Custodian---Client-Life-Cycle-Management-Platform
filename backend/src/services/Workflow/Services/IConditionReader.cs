using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// Read-only view of engagement conditions (CSTD-24 24-N1 contract). Split from IConditionService so
/// consumers that only read conditions (GateEvaluator, NextActionService) do not depend on the write
/// side: ConditionService depends on IClientActionService, which depends on IGateEvaluator, so a gate
/// that depended on IConditionService formed a DI cycle and the service could not start.
/// </summary>
public interface IConditionReader
{
    /// <summary>
    /// Returns active conditions for an engagement, tenant-scoped, ordered by
    /// RequiredBeforeStage and CreatedAt. Used by GateEvaluator and next-action orchestration.
    /// </summary>
    Task<IReadOnlyList<EngagementCondition>> GetActiveConditionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}
