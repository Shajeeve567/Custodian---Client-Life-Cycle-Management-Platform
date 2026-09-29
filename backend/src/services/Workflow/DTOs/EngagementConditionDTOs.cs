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
    public string Type { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string Status { get; set; } = string.Empty;
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
