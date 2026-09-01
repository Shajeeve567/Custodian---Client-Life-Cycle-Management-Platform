using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Custodian.Documents.DTOs;

public class DocumentUploadDto
{
    [Required]
    public IFormFile File { get; set; } = null!;

    [Required]
    public string Type { get; set; } = string.Empty;

    public DateTime? IssueDate { get; set; }

    public DateTime? ExpiryDate { get; set; }

    [Required]
    public string UploaderId { get; set; } = string.Empty;
}
