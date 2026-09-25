using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Sla;

namespace Custodian.Workflow.Services.NextAction;

public sealed class NextActionInputs
{
    public Engagement Engagement { get; init; } = null!;
    public IReadOnlyList<ClientAction> Actions { get; init; } = Array.Empty<ClientAction>();
    public IReadOnlyList<Requirement> Requirements { get; init; } = Array.Empty<Requirement>();
    public IReadOnlyList<DocumentSummaryDto>? Documents { get; init; }
    public bool IsDocumentsUnavailable { get; init; }
    public IReadOnlyList<EngagementCondition> ActiveConditions { get; init; } = Array.Empty<EngagementCondition>();
    public IReadOnlyDictionary<Guid, SlaStatus>? SlaStatuses { get; init; }
    public GateEvaluationResult? NextStageGate { get; init; }
    public bool IsStalled { get; init; }
}
