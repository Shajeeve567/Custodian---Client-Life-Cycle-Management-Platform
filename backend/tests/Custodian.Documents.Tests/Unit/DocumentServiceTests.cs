using System.Text;
using Custodian.Documents.Data;
using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
using Custodian.Shared.Contracts;
using Custodian.Shared.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;


namespace Custodian.Documents.Tests.Unit;

public class DocumentServiceTests
{
    private static DocumentDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new DocumentDbContext(options);
    }

    private static IFormFile CreateValidPdfFormFile(string fileName = "passport.pdf")
    {
        var header = Encoding.UTF8.GetBytes("%PDF-1.4 sample content");
        var stream = new MemoryStream(header);
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.Length).Returns(header.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(header));
        return fileMock.Object;
    }

    [Fact]
    public async Task UploadDocumentAsync_ValidPdf_PersistsMetadataAndReturnsResponse()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-alpha";
        var expectedStoragePath = $"uploads/{tenantId}/{engagementId}/doc.pdf";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync(expectedStoragePath);

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var dto = new DocumentUploadDto
        {
            File = CreateValidPdfFormFile(),
            Type = "Identity",
            IssueDate = DateTime.UtcNow.AddDays(-10),
            ExpiryDate = DateTime.UtcNow.AddYears(5),
            UploaderId = "user-123"
        };

        var response = await service.UploadDocumentAsync(engagementId, tenantId, dto);

        Assert.NotNull(response);
        Assert.NotEqual(Guid.Empty, response.DocumentId);
        Assert.Equal(engagementId, response.EngagementId);
        Assert.Equal(tenantId, response.TenantId);
        Assert.Equal("Identity", response.Type);
        Assert.Equal("passport.pdf", response.FileName);

        // Verify persisted in DbContext
        var persisted = await dbContext.Documents.FirstOrDefaultAsync(d => d.DocumentId == response.DocumentId);
        Assert.NotNull(persisted);
        Assert.Equal(expectedStoragePath, persisted.StoragePath);
        Assert.Equal(tenantId, persisted.TenantId);
    }

    [Fact]
    public async Task UploadDocumentAsync_InvalidFile_ThrowsArgumentException()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var invalidFileMock = new Mock<IFormFile>();
        invalidFileMock.Setup(f => f.FileName).Returns("doc.txt");
        invalidFileMock.Setup(f => f.ContentType).Returns("text/plain");
        invalidFileMock.Setup(f => f.Length).Returns(100);

        var dto = new DocumentUploadDto
        {
            File = invalidFileMock.Object,
            Type = "Contract",
            UploaderId = "user-123"
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadDocumentAsync(Guid.NewGuid(), "tenant-alpha", dto));
    }

    [Fact]
    public async Task GetDocumentsByEngagementAsync_ReturnsMatchingDocuments()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-alpha";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync("uploads/path.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var dto1 = new DocumentUploadDto { File = CreateValidPdfFormFile("doc1.pdf"), Type = "Identity", UploaderId = "u1" };
        var dto2 = new DocumentUploadDto { File = CreateValidPdfFormFile("doc2.pdf"), Type = "Financial", UploaderId = "u2" };

        await service.UploadDocumentAsync(engagementId, tenantId, dto1);
        await service.UploadDocumentAsync(engagementId, tenantId, dto2);

        var results = (await service.GetDocumentsByEngagementAsync(engagementId, tenantId)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Type == "Identity");
        Assert.Contains(results, r => r.Type == "Financial");
    }

    [Fact]
    public async Task GetDocumentByIdAsync_ReturnsMatchingDocument_WhenFound()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-alpha";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync("uploads/path.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var created = await service.UploadDocumentAsync(engagementId, tenantId, new DocumentUploadDto
        {
            File = CreateValidPdfFormFile(),
            Type = "Compliance",
            UploaderId = "u1"
        });

        var result = await service.GetDocumentByIdAsync(engagementId, created.DocumentId, tenantId);

        Assert.NotNull(result);
        Assert.Equal(created.DocumentId, result.DocumentId);
        Assert.Equal("Compliance", result.Type);
    }

    [Fact]
    public async Task UploadDocumentAsync_FreshUtilityBill_PersistsCompliantStatusAndTimestamp()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-beta";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync("uploads/utility.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var dto = new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("utility_bill.pdf"),
            Type = "UtilityBill",
            IssueDate = DateTime.UtcNow.AddDays(-25),
            UploaderId = "user-abc"
        };

        var response = await service.UploadDocumentAsync(engagementId, tenantId, dto);

        Assert.NotNull(response);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Compliant, response.ComplianceStatus);
        Assert.Null(response.RejectionReason);
        Assert.NotNull(response.ValidatedAt);

        // Verify stored in Database
        var persisted = await dbContext.Documents.FindAsync(response.DocumentId);
        Assert.NotNull(persisted);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Compliant, persisted.ComplianceStatus);
        Assert.Null(persisted.RejectionReason);
        Assert.NotNull(persisted.ValidatedAt);
    }

    [Fact]
    public async Task UploadDocumentAsync_OutdatedUtilityBill_PersistsRejectedStatusAndHumanReadableReason()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-beta";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync("uploads/old_bill.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var issueDate = DateTime.UtcNow.AddDays(-120); // 120 days old exceeds 90-day max-age
        var dto = new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("old_utility_bill.pdf"),
            Type = "UtilityBill",
            IssueDate = issueDate,
            UploaderId = "user-abc"
        };

        var response = await service.UploadDocumentAsync(engagementId, tenantId, dto);

        Assert.NotNull(response);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Rejected, response.ComplianceStatus);
        Assert.NotNull(response.RejectionReason);
        Assert.Contains("exceeds maximum allowable age of 90 days", response.RejectionReason);
        Assert.NotNull(response.ValidatedAt);

        // Verify stored in Database with rejection reason preserved
        var persisted = await dbContext.Documents.FindAsync(response.DocumentId);
        Assert.NotNull(persisted);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Rejected, persisted.ComplianceStatus);
        Assert.Equal(response.RejectionReason, persisted.RejectionReason);
        Assert.NotNull(persisted.ValidatedAt);
    }

    [Fact]
    public async Task UploadDocumentAsync_ExpiredPassport_PersistsRejectedStatusAndReason()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-beta";

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), tenantId, engagementId, It.IsAny<Guid>()))
            .ReturnsAsync("uploads/passport.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var expiredDate = DateTime.UtcNow.AddDays(-5);
        var dto = new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("expired_passport.pdf"),
            Type = "Passport",
            IssueDate = DateTime.UtcNow.AddYears(-10),
            ExpiryDate = expiredDate,
            UploaderId = "user-abc"
        };

        var response = await service.UploadDocumentAsync(engagementId, tenantId, dto);

        Assert.NotNull(response);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Rejected, response.ComplianceStatus);
        Assert.NotNull(response.RejectionReason);
        Assert.Contains("Document expired on", response.RejectionReason);
        Assert.Contains(expiredDate.ToString("yyyy-MM-dd"), response.RejectionReason);

        // Verify in Database
        var persisted = await dbContext.Documents.FindAsync(response.DocumentId);
        Assert.NotNull(persisted);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Rejected, persisted.ComplianceStatus);
        Assert.Equal(response.RejectionReason, persisted.RejectionReason);
        Assert.Equal(DocumentVerificationStatus.Unverified, persisted.VerificationStatus);
    }

    [Fact]
    public async Task GetDocumentByIdAsync_WithVerificationFields_MapsAllVerificationProperties()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-gamma";
        var verifiedAt = DateTime.UtcNow;

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-user",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
            StoragePath = "uploads/passport.pdf",
            UploadedAt = DateTime.UtcNow.AddHours(-2),
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            RejectionReason = null,
            ValidatedAt = DateTime.UtcNow.AddHours(-2),
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "staff-reviewer",
            VerifiedAt = verifiedAt,
            VerificationReason = "Physical document cross-checked"
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var result = await service.GetDocumentByIdAsync(engagementId, documentId, tenantId);

        Assert.NotNull(result);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);
        Assert.Equal("staff-reviewer", result.VerifiedBy);
        Assert.Equal(verifiedAt, result.VerifiedAt);
        Assert.Equal("Physical document cross-checked", result.VerificationReason);
    }

    [Fact]
    public async Task VerifyDocumentAsync_CompliantDocument_SetsVerifiedStatusAndKeepsComplianceStatus()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-test";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-1",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 1000,
            StoragePath = "uploads/passport.pdf",
            UploadedAt = DateTime.UtcNow,
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var dto = new VerifyDocumentRequestDto
        {
            StaffNotes = "Manually reviewed and approved.",
            StaffActor = "staff-john"
        };

        var result = await service.VerifyDocumentAsync(engagementId, documentId, tenantId, dto);

        Assert.NotNull(result);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);
        Assert.Equal("staff-john", result.VerifiedBy);
        Assert.Equal("Manually reviewed and approved.", result.VerificationReason);
        Assert.NotNull(result.VerifiedAt);
        // AC 4: Verification remains separate; compliance status is still compliant
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Compliant, result.ComplianceStatus);

        // Verify in DB
        var persisted = await dbContext.Documents.FindAsync(documentId);
        Assert.NotNull(persisted);
        Assert.Equal(DocumentVerificationStatus.Verified, persisted.VerificationStatus);
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Compliant, persisted.ComplianceStatus);
    }

    [Fact]
    public async Task VerifyDocumentAsync_NonCompliantDocument_ThrowsInvalidOperationException()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-test";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-1",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 1000,
            StoragePath = "uploads/passport.pdf",
            UploadedAt = DateTime.UtcNow,
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Rejected,
            RejectionReason = "Expired document",
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var dto = new VerifyDocumentRequestDto { StaffActor = "staff-john" };

        // AC 2: Only auto-compliant documents enter verification
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VerifyDocumentAsync(engagementId, documentId, tenantId, dto));

        Assert.Contains("not automatically compliant", ex.Message);
    }

    [Fact]
    public async Task RejectDocumentVerificationAsync_CompliantDocument_SetsRejectedStatusWithReason()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-test";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-1",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 1000,
            StoragePath = "uploads/passport.pdf",
            UploadedAt = DateTime.UtcNow,
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var dto = new RejectDocumentRequestDto
        {
            Reason = "Photograph is dark and unreadable.",
            StaffActor = "staff-sarah"
        };

        var result = await service.RejectDocumentVerificationAsync(engagementId, documentId, tenantId, dto);

        Assert.NotNull(result);
        Assert.Equal(DocumentVerificationStatus.Rejected, result.VerificationStatus);
        Assert.Equal("Photograph is dark and unreadable.", result.VerificationReason);
        Assert.Equal("staff-sarah", result.VerifiedBy);
        Assert.NotNull(result.VerifiedAt);
        // AC 4: Compliance status is untouched
        Assert.Equal(Custodian.Documents.Compliance.ComplianceStatus.Compliant, result.ComplianceStatus);
    }

    [Fact]
    public async Task RejectDocumentVerificationAsync_EmptyReason_ThrowsArgumentException()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-test";

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var dto = new RejectDocumentRequestDto
        {
            Reason = "   ",
            StaffActor = "staff-sarah"
        };

        // AC 3: Rejection requires a reason
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RejectDocumentVerificationAsync(engagementId, documentId, tenantId, dto));
    }

    [Fact]
    public async Task VerifyDocumentAsync_CrossTenantIsolation_ReturnsNull()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = "tenant-A",
            Type = "Passport",
            UploaderId = "client-1",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 1000,
            StoragePath = "uploads/passport.pdf",
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(dbContext, validator, storageMock.Object);
        var dto = new VerifyDocumentRequestDto { StaffActor = "staff-1" };

        // Attempting to verify from tenant-B
        var result = await service.VerifyDocumentAsync(engagementId, documentId, "tenant-B", dto);
        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyDocumentAsync_PublishesDocumentVerifiedAuditEvent()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();
        var auditMock = new Mock<IAuditPublisher>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-audit";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-user",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 2048,
            StoragePath = "uploads/passport.pdf",
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(
            dbContext,
            validator,
            storageMock.Object,
            complianceEngine: null,
            auditPublisher: auditMock.Object);

        var dto = new VerifyDocumentRequestDto
        {
            StaffNotes = "All details match government database.",
            StaffActor = "staff-auditor"
        };

        var result = await service.VerifyDocumentAsync(engagementId, documentId, tenantId, dto);

        Assert.NotNull(result);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);

        // AC 6: Verify audit event published with correct event type and actor
        auditMock.Verify(
            a => a.PublishEventAsync(
                engagementId,
                tenantId,
                "staff-auditor",
                EventTypes.DocumentVerified,
                It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task RejectDocumentVerificationAsync_PublishesDocumentVerificationRejectedAuditEvent()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();
        var auditMock = new Mock<IAuditPublisher>();

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-audit";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-user",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 2048,
            StoragePath = "uploads/passport.pdf",
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(
            dbContext,
            validator,
            storageMock.Object,
            complianceEngine: null,
            auditPublisher: auditMock.Object);

        var dto = new RejectDocumentRequestDto
        {
            Reason = "Expiry date is obscured by glare.",
            StaffActor = "staff-inspector"
        };

        var result = await service.RejectDocumentVerificationAsync(engagementId, documentId, tenantId, dto);

        Assert.NotNull(result);
        Assert.Equal(DocumentVerificationStatus.Rejected, result.VerificationStatus);

        // AC 6: Verify rejection audit event published with correct event type and actor
        auditMock.Verify(
            a => a.PublishEventAsync(
                engagementId,
                tenantId,
                "staff-inspector",
                EventTypes.DocumentVerificationRejected,
                It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyDocumentAsync_WhenAuditPublisherThrows_DoesNotCorruptOrFailVerification()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();
        var auditMock = new Mock<IAuditPublisher>();

        auditMock
            .Setup(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new HttpRequestException("Audit service unavailable"));

        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-audit";

        var metadata = new Custodian.Documents.Models.DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "Passport",
            UploaderId = "client-user",
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            FileSize = 2048,
            StoragePath = "uploads/passport.pdf",
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant,
            VerificationStatus = DocumentVerificationStatus.Unverified
        };

        dbContext.Documents.Add(metadata);
        await dbContext.SaveChangesAsync();

        var service = new DocumentService(
            dbContext,
            validator,
            storageMock.Object,
            complianceEngine: null,
            auditPublisher: auditMock.Object);

        var dto = new VerifyDocumentRequestDto { StaffActor = "staff-user" };

        // Should NOT throw; business transaction succeeds even if audit fails
        // Wait: does DocumentService handle exception from IAuditPublisher or does AuditPublisher handle it?
        // Let's ensure DocumentService handles any exception from IAuditPublisher gracefully!
        var result = await service.VerifyDocumentAsync(engagementId, documentId, tenantId, dto);

        Assert.NotNull(result);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);
    }
}




