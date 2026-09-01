using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.Models;

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
    public string Type { get; set; } = "DocumentUpload";

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "Pending";

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    public bool IsInternalOnly { get; set; } = false;

    [MaxLength(50)]
    public string AssignedToRole { get; set; } = "Client";

    [MaxLength(100)]
    public string? CompletedByActor { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? SourceMetadata { get; set; }
}
