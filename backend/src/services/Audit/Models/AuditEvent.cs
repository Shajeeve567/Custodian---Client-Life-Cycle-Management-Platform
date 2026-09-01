using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Custodian.Audit.Models;

[Table("events")]
public class AuditEvent
{
    [Key]
    [Column("event_id")]
    [MaxLength(36)]
    public Guid EventId { get; set; } = Guid.NewGuid();

    [Required]
    [Column("engagement_id")]
    [MaxLength(36)]
    public Guid EngagementId { get; set; }

    [Required]
    [Column("tenant_id")]
    [MaxLength(36)]
    public Guid TenantId { get; set; }

    [Required]
    [Column("actor")]
    [MaxLength(255)]
    public string Actor { get; set; } = string.Empty;

    [Required]
    [Column("type")]
    [MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Required]
    [Column("payload", TypeName = "json")]
    public string Payload { get; set; } = "{}";

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("sequence_number")]
    public long SequenceNumber { get; set; }

    [Column("hash")]
    [MaxLength(64)]
    public string? Hash { get; set; }
}
