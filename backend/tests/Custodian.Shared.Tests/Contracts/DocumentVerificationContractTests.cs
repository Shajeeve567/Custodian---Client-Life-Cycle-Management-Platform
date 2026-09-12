using Custodian.Shared.Contracts;
using Custodian.Shared.Messaging;
using Xunit;

namespace Custodian.Shared.Tests.Contracts;

public class DocumentVerificationContractTests
{
    [Fact]
    public void DocumentVerificationStatus_ContainsExpectedConstants()
    {
        Assert.Equal("Unverified", DocumentVerificationStatus.Unverified);
        Assert.Equal("Pending", DocumentVerificationStatus.Pending);
        Assert.Equal("Verified", DocumentVerificationStatus.Verified);
        Assert.Equal("Rejected", DocumentVerificationStatus.Rejected);
    }

    [Fact]
    public void EventTypes_ContainsVerificationEventTypes()
    {
        Assert.Equal("document.verified", EventTypes.DocumentVerified);
        Assert.Equal("document.verification_rejected", EventTypes.DocumentVerificationRejected);
    }

    [Fact]
    public void DocumentVerificationContract_CanBeInstantiatedWithAllFields()
    {
        var docId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var contract = new DocumentVerificationContract(
            DocumentId: docId,
            EngagementId: engagementId,
            TenantId: "tenant-123",
            ActionId: actionId,
            VerificationStatus: DocumentVerificationStatus.Verified,
            VerifiedBy: "staff-456",
            VerificationReason: "Government ID confirmed",
            VerifiedAtUtc: now);

        Assert.Equal(docId, contract.DocumentId);
        Assert.Equal(engagementId, contract.EngagementId);
        Assert.Equal("tenant-123", contract.TenantId);
        Assert.Equal(actionId, contract.ActionId);
        Assert.Equal(DocumentVerificationStatus.Verified, contract.VerificationStatus);
        Assert.Equal("staff-456", contract.VerifiedBy);
        Assert.Equal("Government ID confirmed", contract.VerificationReason);
        Assert.Equal(now, contract.VerifiedAtUtc);
    }

    [Fact]
    public void DocumentComplianceContract_BackwardsCompatibleDefaults_PreservesUnverifiedStatus()
    {
        var docId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Legacy constructor call without verification fields
        var legacyContract = new DocumentComplianceContract(
            DocumentId: docId,
            EngagementId: engagementId,
            TenantId: "tenant-abc",
            ActionId: null,
            DocumentType: "Passport",
            ComplianceStatus: "Compliant",
            RejectionReason: null,
            ValidatedAtUtc: now);

        Assert.Equal(DocumentVerificationStatus.Unverified, legacyContract.VerificationStatus);
        Assert.Null(legacyContract.VerifiedBy);
        Assert.Null(legacyContract.VerificationReason);
        Assert.Null(legacyContract.VerifiedAtUtc);
    }
}
