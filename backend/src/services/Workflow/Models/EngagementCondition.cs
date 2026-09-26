using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Custodian.Workflow.Models;

public static class ConditionType
{
    public const string Approval = "Approval";
    public const string Payment = "Payment";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Approval,
        Payment
    };
}

public static class ConditionStatus
{
    public const string Pending = "Pending";
    public const string Satisfied = "Satisfied";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Pending,
        Satisfied,
        Rejected
    };
}

public static class ConditionPaymentType
{
    public const string Upfront = "Upfront";
    public const string Milestone = "Milestone";
    public const string Final = "Final";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Upfront,
        Milestone,
        Final
    };
}

/// <summary>
/// CSTD-24 (Engagement Condition Management): Represents an optional Approval or Payment
/// condition attached to an engagement that must be satisfied before transitioning into
/// a designated stage (by default, Execution).
/// </summary>
public class EngagementCondition
{
    [Key]
    public Guid ConditionId { get; set; } = Guid.NewGuid();

    [Required]
    public Guid EngagementId { get; set; }

    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Type { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = ConditionStatus.Pending;

    /// <summary>
    /// The stage transition this condition gates (stored as string in DB).
    /// Default is Execution (work cannot start until sign-off/payment is complete).
    /// Must be strictly greater than the engagement's current stage at attach time.
    /// </summary>
    public EngagementStage RequiredBeforeStage { get; set; } = EngagementStage.Execution;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? DueDateUtc { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Amount { get; set; }

    [MaxLength(3)]
    public string? Currency { get; set; }

    [MaxLength(30)]
    public string? PaymentType { get; set; }

    /// <summary>
    /// Staff-only internal note. Never exposed to clients or in audit event payloads.
    /// </summary>
    public string? InternalNote { get; set; }

    [Required]
    [MaxLength(100)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? DeactivatedBy { get; set; }

    public DateTime? DeactivatedAt { get; set; }

    public string? DeactivationReason { get; set; }

    public DateTime? SatisfiedAt { get; set; }

    [MaxLength(100)]
    public string? SatisfiedBy { get; set; }
}
