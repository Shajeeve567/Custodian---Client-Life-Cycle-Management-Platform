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

public class EngagementResponse
{
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string StaffId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
