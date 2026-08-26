namespace Custodian.Documents.DTOs;

public class DocumentResponseDto
{
    public Guid DocumentId { get; set; }
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
    public DateTime UploadedAt { get; set; }
}
