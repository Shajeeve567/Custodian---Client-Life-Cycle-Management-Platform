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
}
