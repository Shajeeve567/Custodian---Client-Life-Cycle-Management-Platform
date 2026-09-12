using System.ComponentModel.DataAnnotations;

namespace Custodian.Documents.DTOs;

public class VerifyDocumentRequestDto
{
    [MaxLength(500, ErrorMessage = "Staff notes cannot exceed 500 characters.")]
    public string? StaffNotes { get; set; }

    [MaxLength(100, ErrorMessage = "Staff actor cannot exceed 100 characters.")]
    public string? StaffActor { get; set; }
}
