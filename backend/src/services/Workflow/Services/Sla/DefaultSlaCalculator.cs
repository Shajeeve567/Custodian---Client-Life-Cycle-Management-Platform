using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Sla;

public class DefaultSlaCalculator : ISlaCalculator
{
    public SlaStatus CalculateActionSla(ClientAction action, DateTimeOffset now)
    {
        if (action.DeadlineUtc.HasValue)
        {
            var isOverdue = action.DeadlineUtc.Value < now.UtcDateTime;
            var overdueBy = isOverdue ? now.UtcDateTime - action.DeadlineUtc.Value : (TimeSpan?)null;
            return new SlaStatus(action.DeadlineUtc.Value, isOverdue, overdueBy);
        }

        return new SlaStatus(null, false, null);
    }

    public SlaStatus CalculateRequirementSla(Requirement requirement, DateTimeOffset now)
    {
        // MVP: Requirements without explicit deadlines use non-overdue baseline
        return new SlaStatus(null, false, null);
    }

    public SlaStatus CalculateConditionSla(EngagementCondition condition, DateTimeOffset now)
    {
        if (condition.DueDateUtc.HasValue)
        {
            var isOverdue = condition.DueDateUtc.Value < now.UtcDateTime;
            var overdueBy = isOverdue ? now.UtcDateTime - condition.DueDateUtc.Value : (TimeSpan?)null;
            return new SlaStatus(condition.DueDateUtc.Value, isOverdue, overdueBy);
        }

        return new SlaStatus(null, false, null);
    }
}
