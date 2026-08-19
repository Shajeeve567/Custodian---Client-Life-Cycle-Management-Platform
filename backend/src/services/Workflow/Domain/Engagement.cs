using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.Domain;

public class Engagement
{
    [Key]
    public Guid EngagementId { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [MaxLength(36)]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(36)]
    public string StaffId { get; set; } = string.Empty;

    [Required]
    public EngagementStatus Status { get; set; } = EngagementStatus.Draft;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ClosedAt { get; set; }
}
