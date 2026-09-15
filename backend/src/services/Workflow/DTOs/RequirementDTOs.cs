using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.DTOs;

public class RequirementResponseDto
{
    public Guid RequirementId { get; set; }
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? StageNumber { get; set; }
    public string? Value { get; set; }
    // Null when returned from the client view (mirrors the CSTD-12 pattern applied to
    // ClientActionResponseDto): internal actor/audit metadata is stripped for client callers.
    public string? AssignedToRole { get; set; }
    public string? RequestedBy { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? RequestedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RequestRequirementDto
{
    [Required]
    [MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [Range(1, 5)]
    public int? StageNumber { get; set; }

    [MaxLength(50)]
    public string AssignedToRole { get; set; } = "Client";

    /// <summary>Human-readable title/description for the mirrored ClientAction (Next Action UI).</summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    public string? Description { get; set; }

    public DateTime? DeadlineUtc { get; set; }

    /// <summary>Populated by the controller from the caller's JWT claim if not supplied.</summary>
    [MaxLength(100)]
    public string? RequestedByActor { get; set; }
}

public class SubmitRequirementDto
{
    [Required]
    public string Value { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? SubmittedByActor { get; set; }
}

public class ReviewRequirementDto
{
    [Required]
    public string Status { get; set; } = RequirementReviewStatus.Approved; // Approved or Rejected

    [Required]
    [MaxLength(100)]
    public string ReviewerActor { get; set; } = string.Empty;

    public string? RejectionReason { get; set; }
}

public static class RequirementReviewStatus
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}
