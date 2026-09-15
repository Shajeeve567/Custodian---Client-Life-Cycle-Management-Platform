using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.Models;

public static class RequirementStatus
{
    public const string Requested = "Requested";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>
/// CSTD-16 (Requirements Collection): a piece of client information staff has requested for
/// an engagement (e.g. "CompanyRegistrationNumber", "SourceOfFunds") — distinct from
/// ClientAction, which is shaped around file-upload/document-verification workflows. This is
/// deliberately independent of CSTD-18's IGateEvaluator; see GateRequirements.cs for why.
///
/// Every Requirement mirrors itself into a ClientAction row (linked via
/// ClientAction.LinkedRequirementId) purely so it surfaces through the existing, already-tested
/// "Next Action" selection in ClientPortalService without that service needing to know this
/// table exists.
/// </summary>
public class Requirement
{
    [Key]
    public Guid RequirementId { get; set; } = Guid.NewGuid();

    [Required]
    public Guid EngagementId { get; set; }

    [Required]
    [MaxLength(36)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = RequirementStatus.Requested;

    [MaxLength(50)]
    public string AssignedToRole { get; set; } = "Client";

    /// <summary>The submitted content. Null until the client submits.</summary>
    public string? Value { get; set; }

    [Range(1, 5)]
    public int? StageNumber { get; set; }

    [MaxLength(100)]
    public string? RequestedBy { get; set; }

    [MaxLength(100)]
    public string? ReviewedBy { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
