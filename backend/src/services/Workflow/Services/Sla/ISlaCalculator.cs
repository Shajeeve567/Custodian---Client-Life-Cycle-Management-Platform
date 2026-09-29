using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Sla;

public interface ISlaCalculator
{
    SlaStatus CalculateActionSla(ClientAction action, DateTimeOffset now);

    /// <summary>
    /// CSTD-37: the action's due time whatever its status: the explicit deadline, else ActivatedAt +
    /// the stage SLA. Unlike <see cref="CalculateActionSla"/>, it also answers for completed and
    /// cancelled actions, so a report can judge on-time vs late with the stall queue's rule.
    /// </summary>
    DateTime ResolveDueAt(ClientAction action);
    SlaStatus CalculateRequirementSla(Requirement requirement, DateTimeOffset now);
    SlaStatus CalculateConditionSla(EngagementCondition condition, DateTimeOffset now);
}
