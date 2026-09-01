using System.ComponentModel.DataAnnotations;

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
    public string Source { get; set; } = string.Empty;
    public bool IsInternalOnly { get; set; }
    public string AssignedToRole { get; set; } = string.Empty;
    public string? CompletedByActor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SourceMetadata { get; set; }
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
