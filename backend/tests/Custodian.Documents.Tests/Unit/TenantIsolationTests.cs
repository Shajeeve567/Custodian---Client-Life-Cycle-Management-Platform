using System.Text;
using Custodian.Documents.Data;
using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class TenantIsolationTests
{
    private static DocumentDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new DocumentDbContext(options);
    }

    private static IFormFile CreateValidPdfFormFile(string fileName = "doc.pdf")
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
    public async Task GetDocumentsByEngagementAsync_TenantA_DoesNotReturnTenantBDocuments()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync((IFormFile f, string tenant, Guid eng, Guid doc) => $"uploads/{tenant}/{eng}/{doc}.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var sharedEngagementId = Guid.NewGuid();
        var tenantA = "tenant-A";
        var tenantB = "tenant-B";

        // Upload doc for Tenant A
        await service.UploadDocumentAsync(sharedEngagementId, tenantA, new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("tenantA_doc.pdf"),
            Type = "Identity",
            UploaderId = "userA"
        });

        // Upload doc for Tenant B
        await service.UploadDocumentAsync(sharedEngagementId, tenantB, new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("tenantB_doc.pdf"),
            Type = "Identity",
            UploaderId = "userB"
        });

        // Query documents as Tenant A
        var tenantAResults = (await service.GetDocumentsByEngagementAsync(sharedEngagementId, tenantA)).ToList();

        // Query documents as Tenant B
        var tenantBResults = (await service.GetDocumentsByEngagementAsync(sharedEngagementId, tenantB)).ToList();

        Assert.Single(tenantAResults);
        Assert.Equal("tenantA_doc.pdf", tenantAResults[0].FileName);
        Assert.Equal(tenantA, tenantAResults[0].TenantId);

        Assert.Single(tenantBResults);
        Assert.Equal("tenantB_doc.pdf", tenantBResults[0].FileName);
        Assert.Equal(tenantB, tenantBResults[0].TenantId);
    }

    [Fact]
    public async Task GetDocumentByIdAsync_TenantA_CannotAccessTenantBDocument()
    {
        using var dbContext = CreateInMemoryDbContext();
        var validator = new DocumentValidator();
        var storageMock = new Mock<IStorageService>();

        storageMock
            .Setup(s => s.SaveFileAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync("uploads/path.pdf");

        var service = new DocumentService(dbContext, validator, storageMock.Object);

        var engagementId = Guid.NewGuid();
        var tenantA = "tenant-A";
        var tenantB = "tenant-B";

        var tenantBDocument = await service.UploadDocumentAsync(engagementId, tenantB, new DocumentUploadDto
        {
            File = CreateValidPdfFormFile("secret_B.pdf"),
            Type = "Financial",
            UploaderId = "userB"
        });

        // Tenant A tries to fetch Tenant B's document ID
        var resultAsTenantA = await service.GetDocumentByIdAsync(engagementId, tenantBDocument.DocumentId, tenantA);

        Assert.Null(resultAsTenantA);
    }
}
