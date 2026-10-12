using System.ComponentModel.DataAnnotations;

namespace Custodian.Audit.DTOs;

/// <summary>
/// CSTD-42: Request body for flagging an audit event.
/// Reason is required, non-empty, and limited to 500 characters.
/// </summary>
public class FlagAuditEventRequest
{
    [Required(ErrorMessage = "Reason is required.", AllowEmptyStrings = false)]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Reason must not exceed 500 characters.")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// CSTD-42: Request body for archiving an audit event.
/// Reason is optional, limited to 500 characters if supplied.
/// </summary>
public class ArchiveAuditEventRequest
{
    [StringLength(500, ErrorMessage = "Reason must not exceed 500 characters.")]
    public string? Reason { get; set; }
}
