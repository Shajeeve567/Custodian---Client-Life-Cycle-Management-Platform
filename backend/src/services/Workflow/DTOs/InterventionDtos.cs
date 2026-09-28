using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.DTOs;

public class RecordInterventionRequest
{
    /// <summary>"Meeting" or "RecoveryAction". Defaults to RecoveryAction.</summary>
    public string Type { get; set; } = "RecoveryAction";

    [Required, MaxLength(2000)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Optional link to the blocking client action.</summary>
    public Guid? BlockerActionId { get; set; }

    /// <summary>Optional link to the persistent stall record.</summary>
    public Guid? StallId { get; set; }
}

public class InterventionResponse
{
    public Guid InterventionId { get; set; }
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid? StallId { get; set; }
    public Guid? BlockerActionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string RecordedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}