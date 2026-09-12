using System.ComponentModel.DataAnnotations;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.DTOs;

public class ClientActionResponseDto
{
    public Guid ActionId { get; set; }
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StageNumber { get; set; } = 1;
    public DateTime? DeadlineUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsInternalOnly { get; set; }
    public string AssignedToRole { get; set; } = string.Empty;
    public string? CompletedByActor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SourceMetadata { get; set; }
    public string? VerificationStatus { get; set; }
    public string? VerificationReason { get; set; }
}

public class CreateClientActionDto
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = "DocumentUpload";

    [Range(1, 5)]
    public int StageNumber { get; set; } = 1;

    public DateTime? DeadlineUtc { get; set; }

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    public bool IsInternalOnly { get; set; } = false;

    [MaxLength(50)]
    public string AssignedToRole { get; set; } = "Client";

    public string? SourceMetadata { get; set; }
}

public class CompleteClientActionDto
{
    [Required]
    [MaxLength(100)]
    public string CompletedByActor { get; set; } = string.Empty;
}

public class UploadActionEvidenceDto
{
    [Required]
    [MaxLength(100)]
    public string UploaderActor { get; set; } = string.Empty;

    public Guid? DocumentId { get; set; }

    public string? ComplianceStatus { get; set; }

    public string? RejectionReason { get; set; }

    public string? VerificationStatus { get; set; }

    public string? VerificationReason { get; set; }

    public string? VerifiedBy { get; set; }
}

public class ReviewActionDto
{
    [Required]
    public string Status { get; set; } = ClientActionStatus.Completed; // Completed or Rejected

    [Required]
    [MaxLength(100)]
    public string ReviewerActor { get; set; } = string.Empty;

    public string? ReviewNote { get; set; }

    public string? VerificationStatus { get; set; }

    public string? VerificationReason { get; set; }
}

public class ApplyActionVerificationDto
{
    [Required]
    public string VerificationStatus { get; set; } = string.Empty; // "Verified" or "Rejected"

    [Required]
    [MaxLength(100)]
    public string VerifiedBy { get; set; } = string.Empty;

    public string? VerificationReason { get; set; }
}

