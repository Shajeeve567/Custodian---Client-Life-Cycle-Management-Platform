using Custodian.Documents.Compliance;
using Custodian.Documents.Data;
using Custodian.Documents.Models;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Documents.Tests.Unit.Reports;

/// <summary>
/// CSTD-180: Unit tests for ValidationVerificationReportService.
/// Verifies tenant isolation, status counting, breakdowns, soft-delete exclusion,
/// and engagement boundary restrictions.
/// </summary>
public class ValidationVerificationReportServiceTests
{
    private static DocumentDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new DocumentDbContext(options);
    }

    private static DocumentMetadata CreateDoc(
        string tenantId,
        Guid engagementId,
        string type = "Passport",
        string complianceStatus = ComplianceStatus.Compliant,
        string verificationStatus = DocumentVerificationStatus.Unverified,
        string? rejectionReason = null,
        string? verificationReason = null,
        bool isDeleted = false)
    {
        return new DocumentMetadata
        {
            DocumentId = Guid.NewGuid(),
            TenantId = tenantId,
            EngagementId = engagementId,
            Type = type,
            ComplianceStatus = complianceStatus,
            VerificationStatus = verificationStatus,
            RejectionReason = rejectionReason,
            VerificationReason = verificationReason,
            IsDeleted = isDeleted,
            UploadedAt = DateTime.UtcNow,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            StoragePath = "test/path.pdf",
            UploaderId = "test-user"
        };
    }

    [Fact]
    public async Task ComputeAggregateAsync_TenantIsolation_TenantADoesNotCountTenantBData()
    {
        using var dbContext = CreateInMemoryDbContext();
        var engA = Guid.NewGuid();
        var engB = Guid.NewGuid();

        dbContext.Documents.AddRange(
            CreateDoc("tenant-a", engA, type: "Passport", complianceStatus: ComplianceStatus.Compliant),
            CreateDoc("tenant-a", engA, type: "UtilityBill", complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Expired"),
            CreateDoc("tenant-b", engB, type: "Financial", complianceStatus: ComplianceStatus.Compliant),
            CreateDoc("tenant-b", engB, type: "Passport", complianceStatus: ComplianceStatus.Compliant)
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        var resultA = await service.ComputeAggregateAsync("tenant-a");
        var resultB = await service.ComputeAggregateAsync("tenant-b");

        // Tenant A assertions
        Assert.Equal("tenant-a", resultA.TenantId);
        Assert.Equal(2, resultA.TotalUploads);
        Assert.False(resultA.IsEmpty);
        Assert.Equal(1, resultA.AutomaticCompliance.Compliant);
        Assert.Equal(1, resultA.AutomaticCompliance.Rejected);
        Assert.Equal(2, resultA.ByDocumentType.Count);
        Assert.Single(resultA.AutomaticRejectionReasons);
        Assert.Equal("Expired", resultA.AutomaticRejectionReasons[0].Reason);
        Assert.DoesNotContain(resultA.ByDocumentType, t => t.Type == "Financial");

        // Tenant B assertions
        Assert.Equal("tenant-b", resultB.TenantId);
        Assert.Equal(2, resultB.TotalUploads);
        Assert.Equal(2, resultB.AutomaticCompliance.Compliant);
        Assert.Equal(0, resultB.AutomaticCompliance.Rejected);
        Assert.Empty(resultB.AutomaticRejectionReasons);
    }

    [Fact]
    public async Task ComputeAggregateAsync_AutomaticComplianceCounts_CompliantRejectedPending()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-compliance";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Expired"),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Pending),
            CreateDoc(tenantId, eng, complianceStatus: "UnknownStatus") // unexpected status
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        Assert.Equal(5, result.TotalUploads);
        Assert.Equal(2, result.AutomaticCompliance.Compliant);
        Assert.Equal(1, result.AutomaticCompliance.Rejected);
        Assert.Equal(1, result.AutomaticCompliance.Pending);
    }

    [Fact]
    public async Task ComputeAggregateAsync_HumanVerificationCounts_VerifiedRejectedUnverifiedPending()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-verification";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Verified, verificationReason: "Approved"),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Verified),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Illegible scan"),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Unverified),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Pending),
            CreateDoc(tenantId, eng, verificationStatus: "ArbitraryStatus") // unexpected status
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        Assert.Equal(6, result.TotalUploads);
        Assert.Equal(2, result.HumanVerification.Verified);
        Assert.Equal(1, result.HumanVerification.Rejected);
        Assert.Equal(1, result.HumanVerification.Unverified);
        Assert.Equal(1, result.HumanVerification.Pending);
    }

    [Fact]
    public async Task ComputeAggregateAsync_DocumentTypeBreakdown_GroupsAndCountsDeterministically()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-types";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, type: "Passport"),
            CreateDoc(tenantId, eng, type: "UtilityBill"),
            CreateDoc(tenantId, eng, type: "Passport"),
            CreateDoc(tenantId, eng, type: "BankStatement"),
            CreateDoc(tenantId, eng, type: "Passport"),
            CreateDoc(tenantId, eng, type: "UtilityBill"),
            CreateDoc(tenantId, eng, type: "IdentityCard")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        Assert.Equal(7, result.TotalUploads);
        Assert.Equal(4, result.ByDocumentType.Count);

        // Deterministic sort: Count DESC, Type ASC
        // Passport: 3
        // UtilityBill: 2
        // BankStatement: 1 (B before I)
        // IdentityCard: 1
        Assert.Equal("Passport", result.ByDocumentType[0].Type);
        Assert.Equal(3, result.ByDocumentType[0].Count);

        Assert.Equal("UtilityBill", result.ByDocumentType[1].Type);
        Assert.Equal(2, result.ByDocumentType[1].Count);

        Assert.Equal("BankStatement", result.ByDocumentType[2].Type);
        Assert.Equal(1, result.ByDocumentType[2].Count);

        Assert.Equal("IdentityCard", result.ByDocumentType[3].Type);
        Assert.Equal(1, result.ByDocumentType[3].Count);
    }

    [Fact]
    public async Task ComputeAggregateAsync_AutomaticRejectionReasons_GroupsAndCountsDeterministically()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-auto-reasons";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Document has expired"),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Document has expired"),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Exceeds 90 days limit"),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant, rejectionReason: null),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Pending, rejectionReason: "   ") // whitespace ignored
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        Assert.Equal(2, result.AutomaticRejectionReasons.Count);

        // Count DESC, Reason ASC
        Assert.Equal("Document has expired", result.AutomaticRejectionReasons[0].Reason);
        Assert.Equal(2, result.AutomaticRejectionReasons[0].Count);

        Assert.Equal("Exceeds 90 days limit", result.AutomaticRejectionReasons[1].Reason);
        Assert.Equal(1, result.AutomaticRejectionReasons[1].Count);
    }

    [Fact]
    public async Task ComputeAggregateAsync_HumanVerificationRejectionReasons_GroupsAndCountsOnlyWhenRejected()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-human-reasons";

        dbContext.Documents.AddRange(
            // Human rejected with reason -> must be counted
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Missing signature"),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Missing signature"),
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Blurry image"),
            // Human verified with notes -> must NOT be counted as a rejection reason
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Verified, verificationReason: "Verified against national registry"),
            // Human unverified with empty reason -> must NOT be counted
            CreateDoc(tenantId, eng, verificationStatus: DocumentVerificationStatus.Unverified, verificationReason: null)
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        Assert.Equal(2, result.HumanVerificationRejectionReasons.Count);

        Assert.Equal("Missing signature", result.HumanVerificationRejectionReasons[0].Reason);
        Assert.Equal(2, result.HumanVerificationRejectionReasons[0].Count);

        Assert.Equal("Blurry image", result.HumanVerificationRejectionReasons[1].Reason);
        Assert.Equal(1, result.HumanVerificationRejectionReasons[1].Count);

        // Verification notes from verified document must not appear
        Assert.DoesNotContain(result.HumanVerificationRejectionReasons, r => r.Reason.Contains("Verified against"));
    }

    [Fact]
    public async Task ComputeAggregateAsync_EmptyTenantData_ReturnsZeroCountsAndEmptyCollections()
    {
        using var dbContext = CreateInMemoryDbContext();
        var service = new ValidationVerificationReportService(dbContext);

        var result = await service.ComputeAggregateAsync("tenant-empty");

        Assert.Equal("tenant-empty", result.TenantId);
        Assert.Equal(0, result.TotalUploads);
        Assert.True(result.IsEmpty);

        Assert.Equal(0, result.AutomaticCompliance.Compliant);
        Assert.Equal(0, result.AutomaticCompliance.Rejected);
        Assert.Equal(0, result.AutomaticCompliance.Pending);

        Assert.Equal(0, result.HumanVerification.Verified);
        Assert.Equal(0, result.HumanVerification.Rejected);
        Assert.Equal(0, result.HumanVerification.Unverified);
        Assert.Equal(0, result.HumanVerification.Pending);

        Assert.Empty(result.ByDocumentType);
        Assert.Empty(result.AutomaticRejectionReasons);
        Assert.Empty(result.HumanVerificationRejectionReasons);
    }

    [Fact]
    public async Task ComputeAggregateAsync_SoftDeletedDocuments_ExcludedFromAllAggregates()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-soft-delete";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, type: "Passport", complianceStatus: ComplianceStatus.Compliant, isDeleted: false),
            CreateDoc(tenantId, eng, type: "UtilityBill", complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Live rejection", isDeleted: false),
            // Soft-deleted documents
            CreateDoc(tenantId, eng, type: "Passport", complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Deleted rejection", isDeleted: true),
            CreateDoc(tenantId, eng, type: "Financial", complianceStatus: ComplianceStatus.Compliant, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Deleted staff reject", isDeleted: true)
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        // Only the 2 non-deleted documents are counted
        Assert.Equal(2, result.TotalUploads);
        Assert.Equal(1, result.AutomaticCompliance.Compliant);
        Assert.Equal(1, result.AutomaticCompliance.Rejected);
        Assert.Equal(2, result.ByDocumentType.Count);
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "Financial");

        Assert.Single(result.AutomaticRejectionReasons);
        Assert.Equal("Live rejection", result.AutomaticRejectionReasons[0].Reason);

        Assert.Empty(result.HumanVerificationRejectionReasons);
    }

    [Fact]
    public async Task ComputeAggregateAsync_RestrictedEngagementIds_OnlyCountsPermittedEngagements()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng1 = Guid.NewGuid();
        var eng2 = Guid.NewGuid();
        var eng3 = Guid.NewGuid();
        const string tenantId = "tenant-restricted";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng1, type: "Passport", complianceStatus: ComplianceStatus.Compliant),
            CreateDoc(tenantId, eng2, type: "UtilityBill", complianceStatus: ComplianceStatus.Compliant),
            CreateDoc(tenantId, eng3, type: "Financial", complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Eng3 reason")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        // Staff only permitted to access eng1 and eng2
        var permittedEngagements = new[] { eng1, eng2 };
        var staffResult = await service.ComputeAggregateAsync(tenantId, permittedEngagements);

        Assert.Equal(2, staffResult.TotalUploads);
        Assert.Equal(2, staffResult.AutomaticCompliance.Compliant);
        Assert.Equal(0, staffResult.AutomaticCompliance.Rejected);
        Assert.Empty(staffResult.AutomaticRejectionReasons);
        Assert.DoesNotContain(staffResult.ByDocumentType, t => t.Type == "Financial");

        // Owner (null allowedEngagementIds) sees all 3 engagements
        var ownerResult = await service.ComputeAggregateAsync(tenantId, allowedEngagementIds: null);
        Assert.Equal(3, ownerResult.TotalUploads);
        Assert.Equal(2, ownerResult.AutomaticCompliance.Compliant);
        Assert.Equal(1, ownerResult.AutomaticCompliance.Rejected);
        Assert.Single(ownerResult.AutomaticRejectionReasons);
        Assert.Contains(ownerResult.ByDocumentType, t => t.Type == "Financial");
    }

    [Fact]
    public async Task ComputeAggregateAsync_EmptyPermittedEngagementsList_ReturnsZeroResults()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-empty-permitted";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, type: "Passport", complianceStatus: ComplianceStatus.Compliant)
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        // Staff member with 0 assigned engagements
        var emptyList = Array.Empty<Guid>();
        var result = await service.ComputeAggregateAsync(tenantId, emptyList);

        Assert.Equal(0, result.TotalUploads);
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.AutomaticCompliance.Compliant);
        Assert.Empty(result.ByDocumentType);
        Assert.Empty(result.AutomaticRejectionReasons);
        Assert.Empty(result.HumanVerificationRejectionReasons);
    }

    [Fact]
    public async Task ComputeAggregateAsync_HumanVerificationEligibility_AutoRejectedAndPendingDocumentsDoNotInflateHumanCounts()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-human-eligibility";

        dbContext.Documents.AddRange(
            // Auto Compliant documents -> eligible for human verification
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant, verificationStatus: DocumentVerificationStatus.Verified),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant, verificationStatus: DocumentVerificationStatus.Unverified),

            // Auto Rejected documents -> NOT eligible; default VerificationStatus="Unverified" must NOT count as human Unverified
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, verificationStatus: DocumentVerificationStatus.Unverified, rejectionReason: "Expired"),
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, verificationStatus: DocumentVerificationStatus.Pending, rejectionReason: "Too old"),

            // Auto Pending documents -> NOT eligible; must NOT count in human verification counts
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Pending, verificationStatus: DocumentVerificationStatus.Unverified)
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        // TotalUploads counts all active documents
        Assert.Equal(5, result.TotalUploads);
        Assert.Equal(2, result.AutomaticCompliance.Compliant);
        Assert.Equal(2, result.AutomaticCompliance.Rejected);
        Assert.Equal(1, result.AutomaticCompliance.Pending);

        // HumanVerification counts ONLY auto-compliant documents (2 total)
        Assert.Equal(1, result.HumanVerification.Verified);
        Assert.Equal(1, result.HumanVerification.Unverified);
        Assert.Equal(0, result.HumanVerification.Rejected);
        Assert.Equal(0, result.HumanVerification.Pending);
    }

    [Fact]
    public async Task ComputeAggregateAsync_RejectionReasonPredicates_ExcludesStaleOrAccidentalReasons()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-stale-reasons";

        dbContext.Documents.AddRange(
            // Valid automatic rejection
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, rejectionReason: "Legitimate auto reject reason"),

            // Accidental/stale RejectionReason on a Compliant document -> must be excluded from AutomaticRejectionReasons
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant, rejectionReason: "Stale auto reject reason on compliant doc"),

            // Accidental/stale RejectionReason on a Pending document -> must be excluded from AutomaticRejectionReasons
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Pending, rejectionReason: "Stale auto reject reason on pending doc"),

            // Valid human rejection (auto-compliant + human rejected)
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Compliant, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Legitimate human reject reason"),

            // Human rejection reason on an auto-rejected document -> must be excluded from HumanVerificationRejectionReasons
            CreateDoc(tenantId, eng, complianceStatus: ComplianceStatus.Rejected, verificationStatus: DocumentVerificationStatus.Rejected, verificationReason: "Invalid human reject on auto-rejected doc")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var result = await service.ComputeAggregateAsync(tenantId);

        // AutomaticRejectionReasons contains ONLY the reason from the ComplianceStatus == Rejected document
        Assert.Single(result.AutomaticRejectionReasons);
        Assert.Equal("Legitimate auto reject reason", result.AutomaticRejectionReasons[0].Reason);
        Assert.Equal(1, result.AutomaticRejectionReasons[0].Count);

        // HumanVerificationRejectionReasons contains ONLY the reason from the ComplianceStatus == Compliant + VerificationStatus == Rejected document
        Assert.Single(result.HumanVerificationRejectionReasons);
        Assert.Equal("Legitimate human reject reason", result.HumanVerificationRejectionReasons[0].Reason);
        Assert.Equal(1, result.HumanVerificationRejectionReasons[0].Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ComputeAggregateAsync_ThrowsArgumentException_WhenTenantIdIsInvalid(string? invalidTenantId)
    {
        using var dbContext = CreateInMemoryDbContext();
        var service = new ValidationVerificationReportService(dbContext);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ComputeAggregateAsync(invalidTenantId!));
    }
}
