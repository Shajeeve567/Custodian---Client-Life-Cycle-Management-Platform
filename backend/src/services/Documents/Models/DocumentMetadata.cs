namespace Custodian.Documents.Models;

public class DocumentMetadata
{
    public Guid DocumentId { get; set; } = Guid.NewGuid();
    public Guid EngagementId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string UploaderId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string ComplianceStatus { get; set; } = Compliance.ComplianceStatus.Pending;
    public string? RejectionReason { get; set; }
    public DateTime? ValidatedAt { get; set; }
}
