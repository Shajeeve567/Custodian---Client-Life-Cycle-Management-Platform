using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.Models;

public static class ClientActionStatus
{
    public const string Pending = "Pending";
    public const string Uploaded = "Uploaded";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public static class ClientActionSourceType
{
    public const string Requirement = "Requirement";
    public const string Document = "Document";
    public const string Condition = "Condition";
    public const string Meeting = "Meeting";
    public const string Lifecycle = "Lifecycle";
    public const string Manual = "Manual";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Requirement,
        Document,
        Condition,
        Meeting,
        Lifecycle,
        Manual
    };
}

public static class ClientActionType
{
    public const string DocumentUpload = "DocumentUpload";
    public const string KycDocument = "KycDocument";
    public const string SignAgreement = "SignAgreement";
    public const string ProofOfAddress = "ProofOfAddress";
    public const string CustomTask = "CustomTask";
    public const string Requirement = "Requirement";
    public const string Approval = "Approval";
    public const string Payment = "Payment";
    public const string Meeting = "Meeting";
}

public class ClientAction
{
    [Key]
    public Guid ActionId { get; set; } = Guid.NewGuid();

    [Required]
    public Guid EngagementId { get; set; }

    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = ClientActionType.DocumentUpload;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = ClientActionStatus.Pending;

    [Range(1, 5)]
    public int StageNumber { get; set; } = 1;

    public DateTime? DeadlineUtc { get; set; }

    /// <summary>
    /// When this action became actionable (start time for SLA calculations).
    /// Null if the action belongs to a stage that has not yet started.
    /// </summary>
    public DateTime? ActivatedAt { get; set; }

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string SourceType { get; set; } = ClientActionSourceType.Manual;

    public bool IsInternalOnly { get; set; } = false;

    [MaxLength(50)]
    public string AssignedToRole { get; set; } = "Client";

    [MaxLength(100)]
    public string? CompletedByActor { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? SourceMetadata { get; set; }

    /// <summary>
    /// CSTD-16: set when this action mirrors a Requirement (see Requirement.cs) purely so it
    /// surfaces through the existing Next Action selection. Null for every other action type.
    /// </summary>
    public Guid? LinkedRequirementId { get; set; }

    public Guid? LinkedDocumentId { get; set; }

    public Guid? LinkedConditionId { get; set; }

    public Guid? LinkedMeetingId { get; set; }
}
