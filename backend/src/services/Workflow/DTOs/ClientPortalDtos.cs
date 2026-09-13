namespace Custodian.Workflow.DTOs;

/// <summary>
/// Aggregated, client-safe dashboard representation of an active engagement for the Client Portal.
/// Strictly excludes internal staff notes, risk evaluations, and technical metadata.
/// </summary>
public class ClientPortalDashboardDto
{
    public Guid EngagementId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Stage progression
    public int CurrentStageNumber { get; set; }
    public string CurrentStageName { get; set; } = string.Empty;
    public string CurrentStageTagline { get; set; } = string.Empty;

    // Milestone gate / condition status
    public string ConditionStatus { get; set; } = string.Empty; // e.g., "ActionRequired", "UnderReview", "AllCaughtUp", "Closed"
    public string ConditionDescription { get; set; } = string.Empty;

    // Progress metric based on total tasks completed
    public int ProgressPercentage { get; set; }
    public int CompletedTasksCount { get; set; }
    public int TotalTasksCount { get; set; }

    // Primary Next Action: Top priority task selected based on active onboarding stage
    public ClientSafeActionDto? PrimaryNextAction { get; set; }

    // Additional pending client-facing tasks (if any)
    public List<ClientSafeActionDto> PendingActions { get; set; } = new();

    // 5-Stage Onboarding Stepper overview
    public List<ClientPortalStageDto> Stages { get; set; } = new();
}

/// <summary>
/// Client-safe representation of an action item.
/// Internal fields (IsInternalOnly, SourceMetadata, staff notes, risk flags) are intentionally absent from this contract.
/// </summary>
public class ClientSafeActionDto
{
    public Guid ActionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty; // e.g., "DocumentUpload", "SignOff"
    public string Status { get; set; } = string.Empty; // "Pending", "Uploaded", "Completed", "Rejected"
    public int StageNumber { get; set; }
    public DateTime? DeadlineUtc { get; set; }
    public bool IsOverdue { get; set; }
    public int? DaysRemaining { get; set; }
    public string? RejectionReason { get; set; }
    public string? VerificationStatus { get; set; }
}

/// <summary>
/// Lifecycle stage representation for the client onboarding stepper.
/// </summary>
public class ClientPortalStageDto
{
    public int StageNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Status { get; set; } = "Upcoming"; // "Completed", "Current", "Upcoming"
}
