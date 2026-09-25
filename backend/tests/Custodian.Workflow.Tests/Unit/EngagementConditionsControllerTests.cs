using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class EngagementConditionsControllerTests
{
    private readonly Mock<IConditionService> _mockConditionService;
    private readonly Mock<IClientActionService> _mockClientActionService;
    private readonly EngagementConditionsController _controller;

    public EngagementConditionsControllerTests()
    {
        _mockConditionService = new Mock<IConditionService>();
        _mockClientActionService = new Mock<IClientActionService>();
        _controller = new EngagementConditionsController(
            _mockConditionService.Object,
            _mockClientActionService.Object,
            NullLogger<EngagementConditionsController>.Instance);
    }

    private void SetupUser(string tenantId, string? role = null, string? clientId = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task AttachCondition_StaffRole_ValidRequest_Returns201Created()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Scope Sign-off",
            RequiredBeforeStage = EngagementStage.Execution
        };

        var expectedDto = new ConditionResponseDto
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = "tenant-001",
            Type = ConditionType.Approval,
            Title = "Scope Sign-off",
            IsActive = true,
            Status = ConditionStatus.Pending
        };

        _mockConditionService.Setup(s => s.AttachConditionAsync(engagementId, "tenant-001", dto, It.IsAny<string>()))
            .ReturnsAsync(expectedDto);

        // Act
        var result = await _controller.AttachCondition(engagementId, dto, tenantId: null);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, createdResult.StatusCode);
        var returnVal = Assert.IsType<ConditionResponseDto>(createdResult.Value);
        Assert.Equal(expectedDto.ConditionId, returnVal.ConditionId);
    }

    [Fact]
    public async Task AttachCondition_ClientRole_Returns403Forbidden()
    {
        // Arrange: Clients are never authorized to attach conditions
        SetupUser("tenant-001", "Client", clientId: "client-1");
        var engagementId = Guid.NewGuid();
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Client attempt",
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act
        var result = await _controller.AttachCondition(engagementId, dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockConditionService.Verify(s => s.AttachConditionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AttachConditionDto>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AttachCondition_CrossTenantRequest_Returns403Forbidden()
    {
        // Arrange: JWT is for tenant-001, but query param requests tenant-999
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Cross-tenant attach",
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act
        var result = await _controller.AttachCondition(engagementId, dto, tenantId: "tenant-999");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task AttachCondition_DuplicateActiveCondition_Returns409Conflict()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Duplicate approval",
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService.Setup(s => s.AttachConditionAsync(engagementId, "tenant-001", dto, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("An active Approval condition already exists for this engagement."));

        // Act
        var result = await _controller.AttachCondition(engagementId, dto, tenantId: null);

        // Assert
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("An active Approval condition already exists", conflict.Value!.ToString());
    }

    [Fact]
    public async Task AttachCondition_ClosedEngagement_Returns409Conflict()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Closed attach",
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService.Setup(s => s.AttachConditionAsync(engagementId, "tenant-001", dto, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Cannot attach conditions to an engagement with status 'Closed'."));

        // Act
        var result = await _controller.AttachCondition(engagementId, dto, tenantId: null);

        // Assert
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task GetConditions_ClientRole_OwnsEngagement_ReturnsClientSafeDtos()
    {
        // Arrange
        SetupUser("tenant-001", "Client", clientId: "client-123");
        var engagementId = Guid.NewGuid();

        _mockClientActionService.Setup(c => c.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-123"))
            .ReturnsAsync(true);

        _mockConditionService.Setup(s => s.GetConditionsClientAsync(engagementId, "tenant-001", "client-123"))
            .ReturnsAsync(new List<ClientSafeConditionDto>
            {
                new() { ConditionId = Guid.NewGuid(), Title = "Approval", Type = "Approval", Status = "Pending" }
            });

        // Act
        var result = await _controller.GetConditions(engagementId, tenantId: null, includeInactive: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
        var list = Assert.IsAssignableFrom<IEnumerable<ClientSafeConditionDto>>(okResult.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task GetConditions_ClientRole_DoesNotOwnEngagement_Returns403Forbidden()
    {
        // Arrange: IDOR check fails
        SetupUser("tenant-001", "Client", clientId: "client-hacker");
        var engagementId = Guid.NewGuid();

        _mockClientActionService.Setup(c => c.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-hacker"))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.GetConditions(engagementId, tenantId: null, includeInactive: null);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdateCondition_InactiveOrSatisfied_Returns409Conflict()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var dto = new UpdateConditionDto { Title = "New Title" };

        _mockConditionService.Setup(s => s.UpdateConditionAsync(engagementId, conditionId, "tenant-001", dto, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Condition can only be updated while active and pending."));

        // Act
        var result = await _controller.UpdateCondition(engagementId, conditionId, dto, tenantId: null);

        // Assert
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task DeactivateCondition_StaffRole_Returns200Ok()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var dto = new DeactivateConditionDto { Reason = "Requirement removed" };

        var expectedDto = new ConditionResponseDto
        {
            ConditionId = conditionId,
            EngagementId = engagementId,
            TenantId = "tenant-001",
            IsActive = false,
            DeactivationReason = "Requirement removed"
        };

        _mockConditionService.Setup(s => s.DeactivateConditionAsync(engagementId, conditionId, "tenant-001", dto, It.IsAny<string>()))
            .ReturnsAsync(expectedDto);

        // Act
        var result = await _controller.DeactivateCondition(engagementId, conditionId, dto, tenantId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var returnVal = Assert.IsType<ConditionResponseDto>(okResult.Value);
        Assert.False(returnVal.IsActive);
    }
}
