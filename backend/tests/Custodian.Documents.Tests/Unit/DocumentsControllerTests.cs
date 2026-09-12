using System.Text;
using Custodian.Documents.Controllers;
using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class DocumentsControllerTests
{
    private readonly Mock<IDocumentService> _documentServiceMock;
    private readonly Mock<IStorageService> _storageServiceMock;
    private readonly Mock<ILogger<DocumentsController>> _loggerMock;
    private readonly DocumentsController _controller;

    public DocumentsControllerTests()
    {
        _documentServiceMock = new Mock<IDocumentService>();
        _storageServiceMock = new Mock<IStorageService>();
        _loggerMock = new Mock<ILogger<DocumentsController>>();
        _controller = new DocumentsController(_documentServiceMock.Object, _storageServiceMock.Object, _loggerMock.Object);

        // Setup HttpContext for Controller
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    private static IFormFile CreateDummyFormFile()
    {
        var content = Encoding.UTF8.GetBytes("%PDF-1.4 sample content");
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("passport.pdf");
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(content));
        return fileMock.Object;
    }

    [Fact]
    public async Task UploadDocument_MissingTenantId_Returns400BadRequest()
    {
        var engagementId = Guid.NewGuid();
        var uploadDto = new DocumentUploadDto { File = CreateDummyFormFile(), Type = "Identity", UploaderId = "user1" };

        var actionResult = await _controller.UploadDocument(engagementId, uploadDto, tenantId: null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task UploadDocument_ValidRequest_WithHeader_Returns201Created()
    {
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-abc";
        _controller.Request.Headers["X-Tenant-ID"] = tenantId;

        var uploadDto = new DocumentUploadDto { File = CreateDummyFormFile(), Type = "Identity", UploaderId = "user1" };
        var responseDto = new DocumentResponseDto
        {
            DocumentId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            Type = "Identity",
            StoragePath = "uploads/tenant-abc/doc.pdf",
            UploadedAt = DateTime.UtcNow,
            UploaderId = "user1"
        };

        _documentServiceMock
            .Setup(s => s.UploadDocumentAsync(engagementId, tenantId, uploadDto))
            .ReturnsAsync(responseDto);

        var actionResult = await _controller.UploadDocument(engagementId, uploadDto, tenantId: null);

        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        Assert.Equal(201, createdResult.StatusCode);
        var returnedDto = Assert.IsType<DocumentResponseDto>(createdResult.Value);
        Assert.Equal(responseDto.DocumentId, returnedDto.DocumentId);
    }

    [Fact]
    public async Task UploadDocument_ServiceThrowsArgumentException_Returns400BadRequest()
    {
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-xyz";
        _controller.Request.Headers["X-Tenant-ID"] = tenantId;

        var uploadDto = new DocumentUploadDto { File = CreateDummyFormFile(), Type = "InvalidType", UploaderId = "user1" };

        _documentServiceMock
            .Setup(s => s.UploadDocumentAsync(engagementId, tenantId, uploadDto))
            .ThrowsAsync(new ArgumentException("Only PDF files are allowed."));

        var actionResult = await _controller.UploadDocument(engagementId, uploadDto, tenantId: null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task GetDocumentsByEngagement_Returns200OkWithList()
    {
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var list = new List<DocumentResponseDto>
        {
            new() { DocumentId = Guid.NewGuid(), EngagementId = engagementId, TenantId = tenantId, FileName = "doc1.pdf", Type = "Identity" }
        };

        _documentServiceMock
            .Setup(s => s.GetDocumentsByEngagementAsync(engagementId, tenantId))
            .ReturnsAsync(list);

        var actionResult = await _controller.GetDocumentsByEngagement(engagementId, tenantId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var returnedList = Assert.IsAssignableFrom<IEnumerable<DocumentResponseDto>>(okResult.Value);
        Assert.Single(returnedList);
    }

    [Fact]
    public async Task GetDocumentById_ExistingDoc_Returns200Ok()
    {
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var docDto = new DocumentResponseDto
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            FileName = "found.pdf"
        };

        _documentServiceMock
            .Setup(s => s.GetDocumentByIdAsync(engagementId, documentId, tenantId))
            .ReturnsAsync(docDto);

        var actionResult = await _controller.GetDocumentById(engagementId, documentId, tenantId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var resultDto = Assert.IsType<DocumentResponseDto>(okResult.Value);
        Assert.Equal(documentId, resultDto.DocumentId);
    }

    [Fact]
    public async Task GetDocumentById_NonExistentDoc_Returns404NotFound()
    {
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        _documentServiceMock
            .Setup(s => s.GetDocumentByIdAsync(engagementId, documentId, tenantId))
            .ReturnsAsync((DocumentResponseDto?)null);

        var actionResult = await _controller.GetDocumentById(engagementId, documentId, tenantId);

        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task DownloadDocument_ExistingFile_ReturnsFileStreamResult()
    {
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";
        var storagePath = "uploads/tenant-1/doc.pdf";

        var docDto = new DocumentResponseDto
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            FileName = "evidence.pdf",
            ContentType = "application/pdf",
            StoragePath = storagePath
        };

        var fileStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 file content"));

        _documentServiceMock
            .Setup(s => s.GetDocumentByIdAsync(engagementId, documentId, tenantId))
            .ReturnsAsync(docDto);

        _storageServiceMock
            .Setup(s => s.GetFileAsync(storagePath))
            .ReturnsAsync(fileStream);

        var actionResult = await _controller.DownloadDocument(engagementId, documentId, tenantId);

        var fileResult = Assert.IsType<FileStreamResult>(actionResult);
        Assert.Equal("application/pdf", fileResult.ContentType);
        Assert.Equal("evidence.pdf", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadDocument_MissingFileInStorage_Returns404NotFound()
    {
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var docDto = new DocumentResponseDto
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            FileName = "missing.pdf",
            StoragePath = "uploads/tenant-1/missing.pdf"
        };

        _documentServiceMock
            .Setup(s => s.GetDocumentByIdAsync(engagementId, documentId, tenantId))
            .ReturnsAsync(docDto);

        _storageServiceMock
            .Setup(s => s.GetFileAsync("uploads/tenant-1/missing.pdf"))
            .ReturnsAsync((Stream?)null);

        var actionResult = await _controller.DownloadDocument(engagementId, documentId, tenantId);

        Assert.IsType<NotFoundObjectResult>(actionResult);
    }

    private void SetUserRole(string role, string userId = "staff-user-1")
    {
        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
            new System.Security.Claims.Claim("tenant_id", "tenant-1")
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "TestAuth");
        _controller.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task VerifyDocument_AuthorizedStaff_Returns200OkWithVerifiedDoc()
    {
        SetUserRole("Staff", "staff-alice");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new VerifyDocumentRequestDto { StaffNotes = "Looks good" };
        var responseDto = new DocumentResponseDto
        {
            DocumentId = documentId,
            VerificationStatus = Custodian.Shared.Contracts.DocumentVerificationStatus.Verified,
            VerifiedBy = "staff-alice",
            ComplianceStatus = Custodian.Documents.Compliance.ComplianceStatus.Compliant
        };

        _documentServiceMock
            .Setup(s => s.VerifyDocumentAsync(engagementId, documentId, tenantId, requestDto))
            .ReturnsAsync(responseDto);

        var actionResult = await _controller.VerifyDocument(engagementId, documentId, requestDto, tenantId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<DocumentResponseDto>(okResult.Value);
        Assert.Equal(Custodian.Shared.Contracts.DocumentVerificationStatus.Verified, result.VerificationStatus);
    }

    [Fact]
    public async Task VerifyDocument_ClientRole_Returns403Forbidden()
    {
        SetUserRole("Client", "client-bob");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new VerifyDocumentRequestDto { StaffNotes = "Trying to verify myself" };

        var actionResult = await _controller.VerifyDocument(engagementId, documentId, requestDto, tenantId);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task VerifyDocument_PreconditionFailed_Returns400BadRequest()
    {
        SetUserRole("Staff", "staff-alice");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new VerifyDocumentRequestDto();

        _documentServiceMock
            .Setup(s => s.VerifyDocumentAsync(engagementId, documentId, tenantId, requestDto))
            .ThrowsAsync(new InvalidOperationException("Document is not automatically compliant."));

        var actionResult = await _controller.VerifyDocument(engagementId, documentId, requestDto, tenantId);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task RejectDocument_AuthorizedStaffWithReason_Returns200Ok()
    {
        SetUserRole("Staff", "staff-alice");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new RejectDocumentRequestDto { Reason = "Document is unreadable." };
        var responseDto = new DocumentResponseDto
        {
            DocumentId = documentId,
            VerificationStatus = Custodian.Shared.Contracts.DocumentVerificationStatus.Rejected,
            VerificationReason = "Document is unreadable."
        };

        _documentServiceMock
            .Setup(s => s.RejectDocumentVerificationAsync(engagementId, documentId, tenantId, requestDto))
            .ReturnsAsync(responseDto);

        var actionResult = await _controller.RejectDocument(engagementId, documentId, requestDto, tenantId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<DocumentResponseDto>(okResult.Value);
        Assert.Equal(Custodian.Shared.Contracts.DocumentVerificationStatus.Rejected, result.VerificationStatus);
    }

    [Fact]
    public async Task RejectDocument_MissingReason_Returns400BadRequest()
    {
        SetUserRole("Staff", "staff-alice");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new RejectDocumentRequestDto { Reason = "" };

        var actionResult = await _controller.RejectDocument(engagementId, documentId, requestDto, tenantId);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task RejectDocument_NonStaffRole_Returns403Forbidden()
    {
        SetUserRole("Client", "client-bob");
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tenantId = "tenant-1";

        var requestDto = new RejectDocumentRequestDto { Reason = "Reason" };

        var actionResult = await _controller.RejectDocument(engagementId, documentId, requestDto, tenantId);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }
}

