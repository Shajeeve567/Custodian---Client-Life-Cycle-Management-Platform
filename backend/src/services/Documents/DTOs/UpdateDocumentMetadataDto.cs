namespace Custodian.Documents.DTOs;

public class UpdateDocumentMetadataDto
{
    public string? Type { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
}
