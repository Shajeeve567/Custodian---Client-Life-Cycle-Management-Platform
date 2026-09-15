namespace Custodian.Documents.DTOs;

public class DocumentFilterDto
{
    public string? Type { get; set; }
    public string? ComplianceStatus { get; set; }
    public string? VerificationStatus { get; set; }
    public string? UploaderId { get; set; }
    public bool IncludeDeleted { get; set; } = false;
}
