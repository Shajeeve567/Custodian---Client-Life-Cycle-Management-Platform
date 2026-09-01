using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientActionsControllerTests
{
    private readonly Mock<IClientActionService> _mockService;
    private readonly Mock<ILogger<ClientActionsController>> _mockLogger;
    private readonly ClientActionsController _controller;

    public ClientActionsControllerTests()
    {
        _mockService = new Mock<IClientActionService>();
        _mockLogger = new Mock<ILogger<ClientActionsController>>();
        _controller = new ClientActionsController(_mockService.Object, _mockLogger.Object);
    }

    private void SetupTenantHeader(string tenantId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task GetActionHistory_ValidTenantHeader_Returns200OK()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var expectedActions = new List<ClientActionResponseDto>
        {
            new ClientActionResponseDto { ActionId = Guid.NewGuid(), Title = "Upload Document", Status = "Pending" }
        };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, "tenant-001", false, null))
                    .ReturnsAsync(expectedActions);

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: false);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var list = Assert.IsAssignableFrom<IEnumerable<ClientActionResponseDto>>(okResult.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task GetActionHistory_MissingTenant_Returns400BadRequest()
    {
        // Arrange: No tenant header, claim, or query param
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task CreateAction_ValidRequest_Returns201Created()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var dto = new CreateClientActionDto
        {
            Title = "Submit Tax ID",
            Type = "InfoSubmission",
            Source = "OnboardingStep1"
        };

        var responseDto = new ClientActionResponseDto
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = "tenant-001",
            Title = dto.Title,
            Status = "Pending"
        };

        _mockService.Setup(s => s.CreateActionAsync(engagementId, "tenant-001", dto))
                    .ReturnsAsync(responseDto);

        // Act
        var result = await _controller.CreateAction(engagementId, dto, tenantId: null);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, createdResult.StatusCode);
        var response = Assert.IsType<ClientActionResponseDto>(createdResult.Value);
        Assert.Equal("Submit Tax ID", response.Title);
    }

    [Fact]
    public async Task CompleteAction_ExistingAction_Returns200OK()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new CompleteClientActionDto { CompletedByActor = "staff-user-01" };

        var responseDto = new ClientActionResponseDto
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = "tenant-001",
            Title = "Completed Action",
            Status = "Completed",
            CompletedByActor = "staff-user-01"
        };

        _mockService.Setup(s => s.CompleteActionAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync(responseDto);

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var response = Assert.IsType<ClientActionResponseDto>(okResult.Value);
        Assert.Equal("Completed", response.Status);
    }

    [Fact]
    public async Task CompleteAction_NonExistentAction_Returns404NotFound()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new CompleteClientActionDto { CompletedByActor = "staff-user-01" };

        _mockService.Setup(s => s.CompleteActionAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync((ClientActionResponseDto?)null);

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: null);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFoundResult.StatusCode);
    }
}
