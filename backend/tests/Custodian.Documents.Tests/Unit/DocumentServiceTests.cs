using System.Text;
using Custodian.Documents.Data;
using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
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
}
