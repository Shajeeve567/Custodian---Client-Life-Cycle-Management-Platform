using System.ComponentModel.DataAnnotations;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.DTOs;

/// <summary>
/// Full staff-facing response DTO including internal note and audit metadata.
/// </summary>
public class ConditionResponseDto
{
    public Guid ConditionId { get; set; }
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? TargetClientId { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ApprovalStatus { get; set; }
    public string RequiredBeforeStage { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? PaymentType { get; set; }
    public string? InternalNote { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? DeactivatedBy { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public string? DeactivationReason { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? SatisfiedAt { get; set; }
    public string? SatisfiedBy { get; set; }
    public bool IsOverdue { get; set; }
}

/// <summary>
/// Client-safe DTO. Internal note, audit history, and internal tenant metadata are strictly stripped.
/// </summary>
public class ClientSafeConditionDto
{
    public Guid ConditionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ApprovalStatus { get; set; }
    public string? RejectionReason { get; set; }
    public string RequiredBeforeStage { get; set; } = string.Empty;
    public DateTime? DueDateUtc { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? PaymentType { get; set; }
    public bool IsOverdue { get; set; }
}

public class AttachConditionDto
{
    [Required]
    [MaxLength(20)]
    public string Type { get; set; } = string.Empty;

    public EngagementStage RequiredBeforeStage { get; set; } = EngagementStage.Execution;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? DueDateUtc { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal? Amount { get; set; }

    [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency must be a valid 3-letter ISO-4217 code (e.g. USD, LKR, EUR).")]
    public string? Currency { get; set; }

    [MaxLength(30)]
    public string? PaymentType { get; set; }

    public string? InternalNote { get; set; }
}

public class UpdateConditionDto
{
    [MaxLength(200)]
    public string? Title { get; set; }

    public string? Description { get; set; }

    public DateTime? DueDateUtc { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal? Amount { get; set; }

    [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency must be a valid 3-letter ISO-4217 code (e.g. USD, LKR, EUR).")]
    public string? Currency { get; set; }

    [MaxLength(30)]
    public string? PaymentType { get; set; }

    public string? InternalNote { get; set; }
}

public class DeactivateConditionDto
{
    [Required(ErrorMessage = "Deactivation reason is required.")]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public class RejectApprovalDto : IValidatableObject
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "Rejection reason is required.")]
    [MinLength(1, ErrorMessage = "Rejection reason cannot be empty.")]
    [MaxLength(500, ErrorMessage = "Rejection reason cannot exceed 500 characters.")]
    public string Reason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            yield return new ValidationResult("Rejection reason cannot be empty or whitespace only.", new[] { nameof(Reason) });
        }
        else if (Reason.Trim().Length > 500)
        {
            yield return new ValidationResult("Rejection reason cannot exceed 500 characters.", new[] { nameof(Reason) });
        }
    }
}

