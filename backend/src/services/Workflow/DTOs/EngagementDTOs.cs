using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.DTOs;

public class CreateEngagementRequest
{
    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [MaxLength(36)]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(36)]
    public string StaffId { get; set; } = string.Empty;
}

public class UpdateEngagementStatusRequest
{
    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Owner only: hands an engagement to another staff member (or owner) of the workspace.</summary>
public class ChangeResponsibleStaffRequest
{
    /// <summary>User id of the new responsible staff member.</summary>
    [Required]
    [MaxLength(36)]
    public string StaffId { get; set; } = string.Empty;
}

public class UpdateEngagementStageRequest
{
    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string Stage { get; set; } = string.Empty;
}

public class EngagementResponse
{
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string StaffId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int StageProgressPercentage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // CSTD-20: handoff and closure context
    public string? HandoffSummary { get; set; }
    public string? HandedOffBy { get; set; }
    public DateTime? HandedOffAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public string? ClosureSummary { get; set; }
}

/// <summary>CSTD-20: staff records the final handoff context for an engagement.</summary>
public class HandoffEngagementRequest
{
    [Required, MaxLength(2000)]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>CSTD-20: staff closes a ready engagement. Summary is optional context.</summary>
public class CloseEngagementRequest
{
    [MaxLength(2000)]
    public string? Summary { get; set; }
}

/// <summary>CSTD-20: readiness evaluation result for staff UI and close/handoff gates.</summary>
public class ReadinessResponse
{
    public Guid EngagementId { get; set; }
    public bool IsReady { get; set; }
    /// <summary>Ready | NotReady | AlreadyClosed | AlreadyCancelled</summary>
    public string State { get; set; } = string.Empty;
    public List<ReadinessBlocker> Blockers { get; set; } = new();
    public DateTime EvaluatedAtUtc { get; set; }
}

public class ReadinessBlocker
{
    /// <summary>Document | Requirement | Condition | Task | External</summary>
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}