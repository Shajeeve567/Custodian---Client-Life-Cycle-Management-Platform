using System.ComponentModel.DataAnnotations;

namespace Custodian.Documents.DTOs;

public class RejectDocumentRequestDto
{
    [Required(ErrorMessage = "Rejection reason is required.")]
    [MinLength(1, ErrorMessage = "Rejection reason cannot be empty.")]
    [MaxLength(500, ErrorMessage = "Rejection reason cannot exceed 500 characters.")]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(100, ErrorMessage = "Staff actor cannot exceed 100 characters.")]
    public string? StaffActor { get; set; }
}
