using Custodian.Audit.Controllers;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

public class AuditEventsControllerTests
{
    private readonly Mock<IAuditEventService> _mockService;
    private readonly AuditEventsController _controller;
    private readonly Guid _testTenantId = Guid.NewGuid();

    public AuditEventsControllerTests()
    {
        _mockService = new Mock<IAuditEventService>();
        _controller = new AuditEventsController(_mockService.Object);

        // Setup HttpContext with authenticated claims
        var claims = new List<Claim>
        {
            new Claim("tenant_id", _testTenantId.ToString()),
            new Claim(ClaimTypes.Name, "testuser@custodian.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    [Fact]
    public async Task CreateEvent_ReturnsCreatedAtAction_WhenValidRequest()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = Guid.NewGuid(),
            Actor = "testuser@custodian.com",
            Type = "Genesis",
            Payload = "{}"
        };

        var response = new AuditEventResponse
        {
            EventId = Guid.NewGuid(),
            EngagementId = request.EngagementId,
            TenantId = _testTenantId,
            Actor = request.Actor,
            Type = request.Type,
            Timestamp = DateTime.UtcNow,
            Payload = "{}"
        };

        _mockService.Setup(s => s.RecordEventAsync(request, _testTenantId))
            .ReturnsAsync(response);

        // Act
        var result = await _controller.CreateEvent(request, null);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var value = Assert.IsType<AuditEventResponse>(createdResult.Value);
        Assert.Equal(response.EventId, value.EventId);
    }

    [Fact]
    public async Task CreateEvent_ReturnsBadRequest_WhenTenantUnresolved()
    {
        // Clear HttpContext user claims
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var request = new CreateAuditEventRequest
        {
            EngagementId = Guid.NewGuid(),
            Actor = "testuser",
            Type = "Genesis",
            Payload = "{}"
        };

        // Act
        var result = await _controller.CreateEvent(request, null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task GetEventById_ReturnsNotFound_WhenEventDoesNotExist()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _mockService.Setup(s => s.GetEventByIdAsync(eventId, _testTenantId))
            .ReturnsAsync((AuditEventResponse?)null);

        // Act
        var result = await _controller.GetEventById(eventId, null);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ==========================================
    // TENANT ISOLATION TESTS (CSTD-12 & CSTD-269)
    // ==========================================

    [Fact]
    public async Task GetEvents_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Act: Authenticated user with _testTenantId queries another tenant ID
        var attackerTenantId = Guid.NewGuid().ToString();
        var result = await _controller.GetEvents(attackerTenantId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetEventsByTenantAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetEvents_WithoutTenantQuery_UsesJwtClaimAndReturns200OK()
    {
        // Arrange
        _mockService.Setup(s => s.GetEventsByTenantAsync(_testTenantId))
            .ReturnsAsync(new List<AuditEventResponse>());

        // Act
        var result = await _controller.GetEvents(null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetEventsByTenantAsync(_testTenantId), Times.Once);
    }

    [Fact]
    public async Task GetEventById_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Act
        var attackerTenantId = Guid.NewGuid().ToString();
        var result = await _controller.GetEventById(Guid.NewGuid(), attackerTenantId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetEventByIdAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetEventsByEngagement_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Act
        var attackerTenantId = Guid.NewGuid().ToString();
        var result = await _controller.GetEventsByEngagement(Guid.NewGuid(), attackerTenantId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetEventsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task VerifyChain_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Act
        var attackerTenantId = Guid.NewGuid().ToString();
        var result = await _controller.VerifyChain(attackerTenantId);

        // Assert
        Assert.IsType<ForbidResult>(result);
        _mockService.Verify(s => s.GetEventsByTenantAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CreateEvent_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = Guid.NewGuid(),
            Actor = "testuser@custodian.com",
            Type = "Genesis",
            Payload = "{}"
        };
        var attackerTenantId = Guid.NewGuid().ToString();

        // Act
        var result = await _controller.CreateEvent(request, attackerTenantId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.RecordEventAsync(It.IsAny<CreateAuditEventRequest>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CreateEvent_MismatchedTenantPayload_Returns403Forbidden()
    {
        // Arrange: Request payload specifies another tenant
        var attackerTenantId = Guid.NewGuid();
        var request = new CreateAuditEventRequest
        {
            EngagementId = Guid.NewGuid(),
            TenantId = attackerTenantId,
            Actor = "testuser@custodian.com",
            Type = "Genesis",
            Payload = "{}"
        };

        // Act
        var result = await _controller.CreateEvent(request, null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.RecordEventAsync(It.IsAny<CreateAuditEventRequest>(), It.IsAny<Guid>()), Times.Never);
    }
}
