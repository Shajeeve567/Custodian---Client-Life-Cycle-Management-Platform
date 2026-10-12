namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-180: Aggregate data model for the Validation &amp; Verification Report.
/// Encapsulates totals, automatic compliance counts, human verification counts,
/// document-type breakdown, and distinct rejection reasons.
/// </summary>
public sealed record ValidationVerificationData(
    string TenantId,
    int TotalUploads,
    AutomaticComplianceCounts AutomaticCompliance,
    HumanVerificationCounts HumanVerification,
    IReadOnlyList<DocumentTypeCount> ByDocumentType,
    IReadOnlyList<RejectionReasonCount> AutomaticRejectionReasons,
    IReadOnlyList<RejectionReasonCount> HumanVerificationRejectionReasons)
{
    public bool IsEmpty => TotalUploads == 0;

    public static ValidationVerificationData Empty(string tenantId) => new(
        tenantId,
        0,
        AutomaticComplianceCounts.Zero,
        HumanVerificationCounts.Zero,
        Array.Empty<DocumentTypeCount>(),
        Array.Empty<RejectionReasonCount>(),
        Array.Empty<RejectionReasonCount>());
}

public sealed record AutomaticComplianceCounts(
    int Compliant,
    int Rejected,
    int Pending)
{
    public static readonly AutomaticComplianceCounts Zero = new(0, 0, 0);
}

public sealed record HumanVerificationCounts(
    int Verified,
    int Rejected,
    int Unverified,
    int Pending)
{
    public static readonly HumanVerificationCounts Zero = new(0, 0, 0, 0);
}

public sealed record DocumentTypeCount(
    string Type,
    int Count);

public sealed record RejectionReasonCount(
    string Reason,
    int Count);
