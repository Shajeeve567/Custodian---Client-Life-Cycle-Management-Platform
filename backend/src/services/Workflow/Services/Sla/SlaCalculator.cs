using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Sla;

/// <summary>
/// CSTD-19 view of the CSTD-33 SLA rule. Action due times come from
/// <see cref="IStallDetectionService.ResolveEffectiveDeadline"/> (explicit deadline, else
/// ActivatedAt + stage SLA from the "Sla" config), so the next-action engine, the portal and the
/// stall queue always agree on when an action is overdue. Pure: no I/O, time is passed in.
/// </summary>
public class SlaCalculator : ISlaCalculator
{
    private readonly IStallDetectionService _stallDetection;

    public SlaCalculator(IStallDetectionService stallDetection)
    {
        _stallDetection = stallDetection;
    }

    public SlaStatus CalculateActionSla(ClientAction action, DateTimeOffset now)
    {
        // Not current (completed, cancelled or its stage not started): only an explicit deadline is shown.
        if (!StallDetectionService.IsSlaApplicable(action))
        {
            return new SlaStatus(action.DeadlineUtc, false, null);
        }

        var dueAt = _stallDetection.ResolveEffectiveDeadline(action);
        var nowUtc = now.UtcDateTime;
        var isOverdue = nowUtc > dueAt; // same strict boundary as StallDetectionService
        return new SlaStatus(dueAt, isOverdue, isOverdue ? nowUtc - dueAt : null);
    }

    public SlaStatus CalculateRequirementSla(Requirement requirement, DateTimeOffset now)
    {
        // Requirements carry no deadline of their own; the engine uses the mirrored action's SLA.
        return new SlaStatus(null, false, null);
    }

    public SlaStatus CalculateConditionSla(EngagementCondition condition, DateTimeOffset now)
    {
        if (condition.DueDateUtc.HasValue)
        {
            var isOverdue = now.UtcDateTime > condition.DueDateUtc.Value;
            var overdueBy = isOverdue ? now.UtcDateTime - condition.DueDateUtc.Value : (TimeSpan?)null;
            return new SlaStatus(condition.DueDateUtc.Value, isOverdue, overdueBy);
        }

        return new SlaStatus(null, false, null);
    }
}
