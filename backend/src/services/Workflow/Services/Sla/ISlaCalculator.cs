using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Sla;

public interface ISlaCalculator
{
    SlaStatus CalculateActionSla(ClientAction action, DateTimeOffset now);
    SlaStatus CalculateRequirementSla(Requirement requirement, DateTimeOffset now);
    SlaStatus CalculateConditionSla(EngagementCondition condition, DateTimeOffset now);
}
