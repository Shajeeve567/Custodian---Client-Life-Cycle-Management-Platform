using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Custodian.Audit.Models;

/// <summary>
/// CSTD-42 (CSTD-241): Non-cryptographic metadata for audit events (flag and archive status).
/// Stored in a separate table so the immutable cryptographic ledger (events table) is never mutated.
/// </summary>
[Table("audit_event_metadata")]
public class AuditEventMetadata
{
    [Key]
    [Column("event_id")]
    [MaxLength(36)]
    public Guid EventId { get; set; }

    [Required]
    [Column("tenant_id")]
    [MaxLength(36)]
    public Guid TenantId { get; set; }

    [Column("is_flagged")]
    public bool IsFlagged { get; set; } = false;

    [Column("flag_reason")]
    [MaxLength(500)]
    public string? FlagReason { get; set; }

    [Column("flagged_by")]
    [MaxLength(255)]
    public string? FlaggedBy { get; set; }

    [Column("flagged_at")]
    public DateTime? FlaggedAt { get; set; }

    [Column("flag_reference_event_id")]
    [MaxLength(36)]
    public Guid? FlagReferenceEventId { get; set; }

    [Column("is_archived")]
    public bool IsArchived { get; set; } = false;

    [Column("archive_reason")]
    [MaxLength(500)]
    public string? ArchiveReason { get; set; }

    [Column("archived_by")]
    [MaxLength(255)]
    public string? ArchivedBy { get; set; }

    [Column("archived_at")]
    public DateTime? ArchivedAt { get; set; }

    [Required]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to the original immutable event.
    /// Foreign key delete behavior is Restrict to prevent deletion of audit evidence.
    /// </summary>
    public AuditEvent? Event { get; set; }
}
