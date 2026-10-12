using Custodian.Documents.Compliance;
using Custodian.Documents.Data;
using Custodian.Documents.Models;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Documents.Tests.Unit.Reports;

/// <summary>
/// CSTD-181: Unit tests for ValidationVerificationFilter date and engagement boundaries.
/// Verifies calendar-day UTC date semantics, reversed date validation,
/// engagement scoping, Staff authorization boundaries, and tenant isolation.
/// </summary>
public class ValidationVerificationFilterTests
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
        DateTime uploadedAtUtc,
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
            UploadedAt = uploadedAtUtc,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            StoragePath = "test/path.pdf",
            UploaderId = "test-user"
        };
    }

    [Fact]
    public async Task ComputeAggregateAsync_FromDate_IncludesDocumentsOnAndAfterBoundary()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-from-date";

        // From date = 2026-10-01 (00:00:00 UTC boundary)
        dbContext.Documents.AddRange(
            // Just before the boundary -> excluded
            CreateDoc(tenantId, eng, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc), type: "Identity"),
            // Exactly on the boundary -> included
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), type: "Passport"),
            // Well after the boundary -> included
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), type: "UtilityBill")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(from: new DateOnly(2026, 10, 1));

        var result = await service.ComputeAggregateAsync(tenantId, filter);

        Assert.Equal(2, result.TotalUploads);
        Assert.Contains(result.ByDocumentType, t => t.Type == "Passport");
        Assert.Contains(result.ByDocumentType, t => t.Type == "UtilityBill");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "Identity");
    }

    [Fact]
    public async Task ComputeAggregateAsync_ToDate_IncludesEntireSelectedUtcDay_AndExcludesAfterBoundary()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-to-date";

        // To date = 2026-10-03 (exclusive upper boundary is 2026-10-04 00:00:00 UTC)
        dbContext.Documents.AddRange(
            // Included: start of day Oct 3
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), type: "Passport"),
            // Included: end of day Oct 3 (23:59:59 UTC)
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 3, 23, 59, 59, DateTimeKind.Utc), type: "UtilityBill"),
            // Excluded: start of next day (2026-10-04 00:00:00 UTC)
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), type: "Financial"),
            // Excluded: later on next day
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 4, 15, 30, 0, DateTimeKind.Utc), type: "BankStatement")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(to: new DateOnly(2026, 10, 3));

        var result = await service.ComputeAggregateAsync(tenantId, filter);

        Assert.Equal(2, result.TotalUploads);
        Assert.Contains(result.ByDocumentType, t => t.Type == "Passport");
        Assert.Contains(result.ByDocumentType, t => t.Type == "UtilityBill");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "Financial");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "BankStatement");
    }

    [Fact]
    public async Task ComputeAggregateAsync_FromAndToRange_FiltersCorrectlyWithinWindow()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-range";

        dbContext.Documents.AddRange(
            // Before range
            CreateDoc(tenantId, eng, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), type: "DocBefore"),
            // In range (day 1)
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), type: "Passport"),
            // In range (day 2)
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc), type: "UtilityBill"),
            // In range (day 3 end)
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc), type: "Financial"),
            // After range
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 4, 0, 0, 1, DateTimeKind.Utc), type: "DocAfter")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(
            from: new DateOnly(2026, 10, 1),
            to: new DateOnly(2026, 10, 3));

        var result = await service.ComputeAggregateAsync(tenantId, filter);

        Assert.Equal(3, result.TotalUploads);
        Assert.Equal(3, result.ByDocumentType.Count);
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "DocBefore");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "DocAfter");
    }

    [Fact]
    public async Task ComputeAggregateAsync_FromEqualsTo_IncludesFullSingleDay()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-single-day";

        dbContext.Documents.AddRange(
            // Day before at 23:59:59 -> excluded
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 1, 23, 59, 59, DateTimeKind.Utc), type: "PrevDay"),
            // Target day at 00:00:00 -> included
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), type: "Passport"),
            // Target day at 12:30:00 -> included
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 2, 12, 30, 0, DateTimeKind.Utc), type: "Passport"),
            // Target day at 23:59:59 -> included
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 2, 23, 59, 59, DateTimeKind.Utc), type: "UtilityBill"),
            // Day after at 00:00:00 -> excluded
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), type: "NextDay")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(
            from: new DateOnly(2026, 10, 2),
            to: new DateOnly(2026, 10, 2));

        var result = await service.ComputeAggregateAsync(tenantId, filter);

        Assert.Equal(3, result.TotalUploads);
        Assert.Equal(2, result.ByDocumentType.FirstOrDefault(t => t.Type == "Passport")?.Count);
        Assert.Equal(1, result.ByDocumentType.FirstOrDefault(t => t.Type == "UtilityBill")?.Count);
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "PrevDay");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "NextDay");
    }

    [Fact]
    public void ValidationVerificationFilter_ReversedDateRange_ThrowsArgumentException()
    {
        var from = new DateOnly(2026, 10, 5);
        var to = new DateOnly(2026, 10, 1);

        var ex = Assert.Throws<ArgumentException>(() => new ValidationVerificationFilter(from, to));
        Assert.Contains("cannot be after", ex.Message);
    }

    [Fact]
    public async Task ComputeAggregateAsync_ReversedDateRange_RejectedByService()
    {
        using var dbContext = CreateInMemoryDbContext();
        var service = new ValidationVerificationReportService(dbContext);

        // Created with valid/null then mutated or validated via service
        var filter = new ValidationVerificationFilter
        {
            From = new DateOnly(2026, 10, 10),
            To = new DateOnly(2026, 10, 2)
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ComputeAggregateAsync("tenant-test", filter));
        Assert.Contains("cannot be after", ex.Message);
    }

    [Fact]
    public async Task ComputeAggregateAsync_EngagementId_OnlyCountsRequestedEngagement()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng1 = Guid.NewGuid();
        var eng2 = Guid.NewGuid();
        const string tenantId = "tenant-eng-filter";
        var now = DateTime.UtcNow;

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng1, now, type: "Passport"),
            CreateDoc(tenantId, eng1, now, type: "UtilityBill"),
            CreateDoc(tenantId, eng2, now, type: "Financial")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(engagementId: eng1);

        // Owner call: allowedEngagementIds is null
        var result = await service.ComputeAggregateAsync(tenantId, filter, allowedEngagementIds: null);

        Assert.Equal(2, result.TotalUploads);
        Assert.Contains(result.ByDocumentType, t => t.Type == "Passport");
        Assert.Contains(result.ByDocumentType, t => t.Type == "UtilityBill");
        Assert.DoesNotContain(result.ByDocumentType, t => t.Type == "Financial");
    }

    [Fact]
    public async Task ComputeAggregateAsync_EngagementFilterAndStaffAllowedEngagements_IntersectsSafely()
    {
        using var dbContext = CreateInMemoryDbContext();
        var engA = Guid.NewGuid();
        var engB = Guid.NewGuid();
        var engC = Guid.NewGuid();
        const string tenantId = "tenant-staff-intersect";
        var now = DateTime.UtcNow;

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, engA, now, type: "Passport"),
            CreateDoc(tenantId, engB, now, type: "UtilityBill"),
            CreateDoc(tenantId, engC, now, type: "Financial")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        // Staff allowed [engA, engB], requests filter for engA
        var staffAllowed = new[] { engA, engB };
        var filter = new ValidationVerificationFilter(engagementId: engA);

        var result = await service.ComputeAggregateAsync(tenantId, filter, allowedEngagementIds: staffAllowed);

        Assert.Equal(1, result.TotalUploads);
        Assert.Single(result.ByDocumentType);
        Assert.Equal("Passport", result.ByDocumentType[0].Type);
    }

    [Fact]
    public async Task ComputeAggregateAsync_RequestedEngagementOutsideStaffAllowedIds_ReturnsZeroResults()
    {
        using var dbContext = CreateInMemoryDbContext();
        var engA = Guid.NewGuid();
        var engB = Guid.NewGuid();
        var engC = Guid.NewGuid();
        const string tenantId = "tenant-unauthorized-eng";
        var now = DateTime.UtcNow;

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, engA, now, type: "Passport"),
            CreateDoc(tenantId, engB, now, type: "UtilityBill"),
            CreateDoc(tenantId, engC, now, type: "Financial")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        // Staff allowed [engA, engB], but attempts to query engC
        var staffAllowed = new[] { engA, engB };
        var filter = new ValidationVerificationFilter(engagementId: engC);

        var result = await service.ComputeAggregateAsync(tenantId, filter, allowedEngagementIds: staffAllowed);

        // Security rule: returns empty aggregate immediately without leaking engC's existence or counts
        Assert.Equal(0, result.TotalUploads);
        Assert.True(result.IsEmpty);
        Assert.Empty(result.ByDocumentType);
        Assert.Empty(result.AutomaticRejectionReasons);
        Assert.Empty(result.HumanVerificationRejectionReasons);
    }

    [Fact]
    public async Task ComputeAggregateAsync_TenantIsolation_HoldsWithFilters()
    {
        using var dbContext = CreateInMemoryDbContext();
        var sharedEngId = Guid.NewGuid();
        var targetDate = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        dbContext.Documents.AddRange(
            CreateDoc("tenant-alpha", sharedEngId, targetDate, type: "Passport"),
            CreateDoc("tenant-beta", sharedEngId, targetDate, type: "Passport")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);
        var filter = new ValidationVerificationFilter(
            from: new DateOnly(2026, 10, 1),
            to: new DateOnly(2026, 10, 3),
            engagementId: sharedEngId);

        var resultAlpha = await service.ComputeAggregateAsync("tenant-alpha", filter);
        var resultBeta = await service.ComputeAggregateAsync("tenant-beta", filter);

        Assert.Equal(1, resultAlpha.TotalUploads);
        Assert.Equal("tenant-alpha", resultAlpha.TenantId);

        Assert.Equal(1, resultBeta.TotalUploads);
        Assert.Equal("tenant-beta", resultBeta.TenantId);
    }

    [Fact]
    public async Task ComputeAggregateAsync_EmptyFilteredResult_ReturnsZeroEmptyAggregate()
    {
        using var dbContext = CreateInMemoryDbContext();
        var eng = Guid.NewGuid();
        const string tenantId = "tenant-empty-filter";

        dbContext.Documents.AddRange(
            CreateDoc(tenantId, eng, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), type: "Passport")
        );
        await dbContext.SaveChangesAsync();

        var service = new ValidationVerificationReportService(dbContext);

        // Filter window where no documents exist
        var filter = new ValidationVerificationFilter(
            from: new DateOnly(2026, 11, 1),
            to: new DateOnly(2026, 11, 30));

        var result = await service.ComputeAggregateAsync(tenantId, filter);

        Assert.Equal(0, result.TotalUploads);
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.AutomaticCompliance.Compliant);
        Assert.Equal(0, result.HumanVerification.Verified);
        Assert.Empty(result.ByDocumentType);
        Assert.Empty(result.AutomaticRejectionReasons);
        Assert.Empty(result.HumanVerificationRejectionReasons);
    }
}
