using System.ComponentModel.DataAnnotations;

namespace Custodian.Audit.DTOs;

public class CreateAuditEventRequest
{
    // Optional: when supplied (e.g. by the Kafka consumer, carrying the
    // envelope's EventId through), enables idempotent recording — a redelivered
    // message with the same EventId won't create a duplicate row. HTTP callers
    // leave this null, preserving today's always-generate-a-new-id behavior.
    public Guid? EventId { get; set; }

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
    public string? PreviousHash { get; set; }

    // Non-cryptographic metadata (CSTD-42 / CSTD-241)
    public bool IsFlagged { get; set; } = false;
    public string? FlagReason { get; set; }
    public string? FlaggedBy { get; set; }
    public DateTime? FlaggedAt { get; set; }
    public Guid? FlagReferenceEventId { get; set; }
    public bool IsArchived { get; set; } = false;
    public string? ArchiveReason { get; set; }
    public string? ArchivedBy { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

public class ChainVerificationResult
{
    public Guid? EngagementId { get; set; }
    public bool IsVerified { get; set; }
    public int Count { get; set; }
    public Guid? BrokenAtEventId { get; set; }
    public string? Reason { get; set; }
}