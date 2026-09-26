namespace Custodian.Workflow.Services.Sla;

public sealed record SlaStatus(
    DateTime? DueAtUtc,
    bool IsOverdue,
    TimeSpan? OverdueBy);
