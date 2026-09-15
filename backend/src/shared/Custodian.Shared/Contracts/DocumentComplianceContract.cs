namespace Custodian.Shared.Contracts;

public sealed record DocumentComplianceContract(
    Guid DocumentId,
    Guid EngagementId,
    string TenantId,
    Guid? ActionId,
    string DocumentType,
    string ComplianceStatus,
    string? RejectionReason,
    DateTime ValidatedAtUtc,
    string VerificationStatus = DocumentVerificationStatus.Unverified,
    string? VerifiedBy = null,
    string? VerificationReason = null,
    DateTime? VerifiedAtUtc = null);

