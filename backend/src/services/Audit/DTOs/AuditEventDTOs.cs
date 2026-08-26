using System.ComponentModel.DataAnnotations;

namespace Custodian.Audit.DTOs;

public class CreateAuditEventRequest
{
    [Required]
    public Guid EngagementId { get; set; }

    public Guid? TenantId { get; set; }

    [Required]
    public string Actor { get; set; } = string.Empty;

    [Required]
    public string Type { get; set; } = string.Empty;

    [Required]
    public string Payload { get; set; } = "{}";
}

public class AuditEventResponse
{
    public Guid EventId { get; set; }
    public Guid EngagementId { get; set; }
    public Guid TenantId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Payload { get; set; } = "{}";
    public long SequenceNumber { get; set; }
    public string? Hash { get; set; }
}
