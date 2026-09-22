namespace Custodian.Workflow.DTOs;

// Engagement's stall state computed on read from 
// curretn time vs deadline
public sealed class StallStatusDto
{
    public Guid EngagementId { get; set; }
    public bool IsStalled { get; set; }
    public Guid? ActionId { get; set; }
    public string? ActionTitle { get; set; }
    public int? StageNumber { get; set; }
    public DateTime? DeadlineUtc { get; set; }
    public int? HoursOverdue { get; set; }
    public DateTime EvaluatedAtUtc { get; set; }
}