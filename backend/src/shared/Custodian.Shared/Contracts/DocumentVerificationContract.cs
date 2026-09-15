namespace Custodian.Shared.Contracts;

public static class DocumentVerificationStatus
{
    public const string Unverified = "Unverified";
    public const string Pending = "Pending";
    public const string Verified = "Verified";
    public const string Rejected = "Rejected";
}

public sealed record DocumentVerificationContract(
    Guid DocumentId,
    Guid EngagementId,
    string TenantId,
    Guid? ActionId,
    string VerificationStatus,
    string? VerifiedBy,
    string? VerificationReason,
    DateTime? VerifiedAtUtc);
