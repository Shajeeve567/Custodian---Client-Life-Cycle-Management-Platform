using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientPortalControllerTests
{
    private readonly Mock<IClientPortalService> _mockPortalService;
    private readonly Mock<ILogger<ClientPortalController>> _mockLogger;
    private readonly ClientPortalController _controller;

    public ClientPortalControllerTests()
    {
        _mockPortalService = new Mock<IClientPortalService>();
        _mockLogger = new Mock<ILogger<ClientPortalController>>();
        _controller = new ClientPortalController(_mockPortalService.Object, _mockLogger.Object);
    }

    private void SetupUserContext(string tenantId, string? clientId = null, string? role = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;

        var claims = new List<Claim>
        {
            new Claim("tenant_id", tenantId)
        };

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            httpContext.Request.Headers["X-Client-ID"] = clientId;
            claims.Add(new Claim("client_id", clientId));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, clientId));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task GetMyActiveEngagement_ValidContext_Returns200OK()
    {
        // Arrange
        var tenantId = "tenant-001";
        var clientId = "client-user-1";
        SetupUserContext(tenantId, clientId, "Client");

        var expectedDto = new ClientPortalDashboardDto
        {
            EngagementId = Guid.NewGuid(),
            CurrentStageNumber = 1,
            CurrentStageName = "Intake & Onboarding",
            ProgressPercentage = 25
        };

        _mockPortalService.Setup(s => s.GetActiveDashboardForClientAsync(tenantId, clientId))
            .ReturnsAsync(expectedDto);

        // Act
        var result = await _controller.GetMyActiveEngagement(tenantId: null, clientId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var dto = Assert.IsType<ClientPortalDashboardDto>(okResult.Value);
        Assert.Equal(expectedDto.EngagementId, dto.EngagementId);
        Assert.Equal(25, dto.ProgressPercentage);
    }

    [Fact]
    public async Task GetMyActiveEngagement_MissingClientId_Returns400BadRequest()
    {
        // Arrange: Missing client identity
        SetupUserContext("tenant-001", clientId: null);

        // Act
        var result = await _controller.GetMyActiveEngagement(tenantId: null, clientId: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task GetMyActiveEngagement_NoActiveEngagementFound_Returns404NotFound()
    {
        // Arrange
        var tenantId = "tenant-001";
        var clientId = "client-user-1";
        SetupUserContext(tenantId, clientId, "Client");

        _mockPortalService.Setup(s => s.GetActiveDashboardForClientAsync(tenantId, clientId))
            .ReturnsAsync((ClientPortalDashboardDto?)null);

        // Act
        var result = await _controller.GetMyActiveEngagement(tenantId: null, clientId: null);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task GetEngagementDashboard_ClientAccessesOwnEngagement_Returns200OK()
    {
        // Arrange
        var tenantId = "tenant-001";
        var clientId = "client-owner-456";
        var engagementId = Guid.NewGuid();
        SetupUserContext(tenantId, clientId, "Client");

        var expectedDto = new ClientPortalDashboardDto
        {
            EngagementId = engagementId,
            CurrentStageNumber = 2,
            ProgressPercentage = 50
        };

        _mockPortalService.Setup(s => s.GetDashboardForEngagementAsync(engagementId, tenantId, clientId))
            .ReturnsAsync(expectedDto);

        // Act
        var result = await _controller.GetEngagementDashboard(engagementId, tenantId: null, clientId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var dto = Assert.IsType<ClientPortalDashboardDto>(okResult.Value);
        Assert.Equal(engagementId, dto.EngagementId);
    }

    [Fact]
    public async Task GetEngagementDashboard_ClientAccessesAnotherClientEngagement_Returns403Forbidden()
    {
        // Arrange
        var tenantId = "tenant-001";
        var attackerClient = "client-attacker-999";
        var targetEngagementId = Guid.NewGuid();
        SetupUserContext(tenantId, attackerClient, "Client");

        // 1. Calling with attacker's clientId returns null (ownership check fails)
        _mockPortalService.Setup(s => s.GetDashboardForEngagementAsync(targetEngagementId, tenantId, attackerClient))
            .ReturnsAsync((ClientPortalDashboardDto?)null);

        // 2. Unconstrained check shows engagement DOES exist under another client!
        _mockPortalService.Setup(s => s.GetDashboardForEngagementAsync(targetEngagementId, tenantId, null))
            .ReturnsAsync(new ClientPortalDashboardDto { EngagementId = targetEngagementId });

        // Act
        var result = await _controller.GetEngagementDashboard(targetEngagementId, tenantId: null, clientId: null);

        // Assert: Access denied per AC #4 (Client only accesses own engagement)
        var objResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public async Task GetEngagementDashboard_NonExistentEngagement_Returns404NotFound()
    {
        // Arrange
        var tenantId = "tenant-001";
        var clientId = "client-user-1";
        var nonExistentId = Guid.NewGuid();
        SetupUserContext(tenantId, clientId, "Client");

        _mockPortalService.Setup(s => s.GetDashboardForEngagementAsync(nonExistentId, tenantId, clientId))
            .ReturnsAsync((ClientPortalDashboardDto?)null);

        _mockPortalService.Setup(s => s.GetDashboardForEngagementAsync(nonExistentId, tenantId, null))
            .ReturnsAsync((ClientPortalDashboardDto?)null);

        // Act
        var result = await _controller.GetEngagementDashboard(nonExistentId, tenantId: null, clientId: null);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
    }
}
