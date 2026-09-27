namespace Custodian.Workflow.DTOs;

public class StallQueueItemDto
{
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string StaffId { get; set; } = string.Empty;

    /// <summary>
    /// Engagement stage name
    /// Example: "Verfication"
    /// </summary>
    public string EngagementStage { get; set; } = string.Empty;

    public Guid BlockerActionId { get; set; }
    public string BlockerActionTitle { get; set; } = string.Empty;
    public int BlockerStageNumber { get; set; }


    /// <summary>
    /// Advisory next step for the responsible staff member. Not a state
    /// transition — the blocked action remains Pending until it is completed
    /// or the client uploads evidence through the existing endpoints.
    /// </summary>
    public string NextAction { get; set; } = string.Empty;

    /// <summary>Who owns the next action (Client | Staff) when it comes from the next-action engine; null otherwise.</summary>
    public string? NextActionResponsibleParty { get; set; }

    public DateTime DeadlineUtc { get; set; }
    public int HoursOverdue { get; set; }

    /// <summary>When the engagement's earliest open stall was first detected (persisted stall record).</summary>
    public DateTime StalledSinceUtc { get; set; }

    /// <summary>Open stall episodes on the engagement.</summary>
    public int OpenStallCount { get; set; }

    /// <summary>CSTD-34-2: hours overdue x weight (stage-gating blockers weigh more). The queue is sorted by it.</summary>
    public double UrgencyScore { get; set; }
    public DateTime EvaluatedAtUtc { get; set; }
}