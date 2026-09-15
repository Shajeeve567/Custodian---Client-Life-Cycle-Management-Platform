using System.Security.Claims;
using Custodian.Shared.Reporting.Reports;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for ReportsController. Mirrors EngagementsControllerUnitTests' style: mock
/// IEngagementRepository and HttpContext to exercise tenant isolation via JWT claims.
/// ReportGenerator has no dependencies of its own, so a real instance is used rather than
/// a mock — only the repository and the HTTP/claims context need faking.
/// </summary>
public class ReportsControllerTests
{
    private readonly Mock<IEngagementRepository> _mockRepo;
    private readonly ReportsController _controller;

    public ReportsControllerTests()
    {
        _mockRepo = new Mock<IEngagementRepository>();
        _controller = new ReportsController(_mockRepo.Object, new ReportGenerator());
    }

    private void SetupUserJwtClaim(string tenantIdClaim)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantIdClaim)
        }, "TestAuthType"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private static List<Engagement> SampleEngagements(string tenantId) => new()
    {
        new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }
    };

    [Fact]
    public async Task GetEngagementReport_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange (tenant isolation — the exact leak this fix closes): an authenticated
        // caller from tenant-A must not be able to pull tenant-B's report by just changing
        // the query param.
        SetupUserJwtClaim("tenant-A");

        // Act
        var result = await _controller.GetEngagementReport(tenantId: "tenant-B", format: "csv");

        // Assert
        Assert.IsType<ForbidResult>(result);
        _mockRepo.Verify(r => r.GetAllByTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetEngagementReport_NoTenantIdentification_ReturnsBadRequest()
    {
        // Arrange: no JWT claim, no query param, no header
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.GetEngagementReport(tenantId: null, format: "csv");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockRepo.Verify(r => r.GetAllByTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetEngagementReport_JwtClaimTenant_UsesClaimOverQueryParam()
    {
        // Arrange: claim and query param agree — should proceed using that tenant
        SetupUserJwtClaim("tenant-A");
        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-A"))
            .ReturnsAsync(SampleEngagements("tenant-A"));

        // Act
        var result = await _controller.GetEngagementReport(tenantId: "tenant-A", format: "csv");

        // Assert
        Assert.IsType<FileContentResult>(result);
        _mockRepo.Verify(r => r.GetAllByTenantAsync("tenant-A"), Times.Once);
    }

    [Fact]
    public async Task GetEngagementReport_ValidRequest_ReturnsCsvFileWithCorrectContentType()
    {
        // Arrange
        SetupUserJwtClaim("tenant-A");
        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-A"))
            .ReturnsAsync(SampleEngagements("tenant-A"));

        // Act
        var result = await _controller.GetEngagementReport(tenantId: null, format: "csv");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", fileResult.ContentType);
        Assert.Equal("engagement-summary.csv", fileResult.FileDownloadName);
        Assert.Contains("Engagement ID,Client ID,Staff ID,Status,Stage,Created At,Closed At",
            System.Text.Encoding.UTF8.GetString(fileResult.FileContents));
    }

    [Fact]
    public async Task GetEngagementReport_ValidRequest_ReturnsJsonFileWithCorrectContentType()
    {
        // Arrange
        SetupUserJwtClaim("tenant-A");
        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-A"))
            .ReturnsAsync(SampleEngagements("tenant-A"));

        // Act
        var result = await _controller.GetEngagementReport(tenantId: null, format: "json");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json; charset=utf-8", fileResult.ContentType);
        Assert.Equal("engagement-summary.json", fileResult.FileDownloadName);
        Assert.Contains("\"Client ID\":\"client-1\"", System.Text.Encoding.UTF8.GetString(fileResult.FileContents));
    }

    [Fact]
    public async Task GetEngagementReport_InvalidFormat_ReturnsBadRequest()
    {
        // Arrange
        SetupUserJwtClaim("tenant-A");
        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-A"))
            .ReturnsAsync(SampleEngagements("tenant-A"));

        // Act
        var result = await _controller.GetEngagementReport(tenantId: null, format: "xml");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
