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

public class RequirementsControllerTests
{
    private readonly Mock<IRequirementService> _mockService;
    private readonly Mock<ILogger<RequirementsController>> _mockLogger;
    private readonly RequirementsController _controller;

    public RequirementsControllerTests()
    {
        _mockService = new Mock<IRequirementService>();
        _mockLogger = new Mock<ILogger<RequirementsController>>();
        _controller = new RequirementsController(_mockService.Object, _mockLogger.Object);
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
    public async Task GetRequirements_NoExplicitViewRequest_DefaultsToClientView()
    {
        // Arrange: same CSTD-12 "safe default" convention as ClientActionsController
        SetupUser("tenant-001");
        var engagementId = Guid.NewGuid();

        _mockService.Setup(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", true, null))
            .ReturnsAsync(new List<RequirementResponseDto>());

        // Act
        var result = await _controller.GetRequirements(engagementId, tenantId: null, isClientView: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", true, null), Times.Once);
    }

    [Fact]
    public async Task GetRequirements_ClientRoleRequestsStaffView_ForcesClientViewTrue()
    {
        // Arrange
        SetupUser("tenant-001", "Client", clientId: "client-1");
        var engagementId = Guid.NewGuid();

        _mockService.Setup(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", true, "client-1"))
            .ReturnsAsync(new List<RequirementResponseDto>());

        // Act: Client attempts to request the staff view
        var result = await _controller.GetRequirements(engagementId, tenantId: null, isClientView: false);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", true, "client-1"), Times.Once);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", false, "client-1"), Times.Never);
    }

    [Fact]
    public async Task GetRequirements_ClientRoleWithoutResolvableIdentity_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection): a Client-role caller with no resolvable identity
        // claim must be denied outright rather than let the query run unconstrained.
        SetupUser("tenant-001", "Client");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetRequirements(engagementId, tenantId: null, isClientView: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetRequirements_NoRecognizedRoleExplicitlyRequestsStaffView_Returns403Forbidden()
    {
        // Arrange: a roleless caller must never be able to escalate to the staff view
        SetupUser("tenant-001");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetRequirements(engagementId, tenantId: null, isClientView: false);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetRequirements_StaffRoleRequestsStaffView_AllowsStaffView()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();

        _mockService.Setup(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", false, null))
            .ReturnsAsync(new List<RequirementResponseDto>());

        // Act
        var result = await _controller.GetRequirements(engagementId, tenantId: null, isClientView: false);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(s => s.GetRequirementsByEngagementAsync(engagementId, "tenant-001", false, null), Times.Once);
    }

    [Fact]
    public async Task GetRequirements_CrossTenantQueryParam_Returns403Forbidden()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetRequirements(engagementId, tenantId: "tenant-other", isClientView: false);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task RequestRequirement_ClientRole_Returns403Forbidden()
    {
        // Arrange: only staff/owner may request a requirement
        SetupUser("tenant-001", "Client");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.RequestRequirement(engagementId, new RequestRequirementDto { Type = "SourceOfFunds" }, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.RequestRequirementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<RequestRequirementDto>()), Times.Never);
    }

    [Fact]
    public async Task RequestRequirement_StaffRole_CallsServiceAndReturnsCreated()
    {
        // Arrange
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var dto = new RequestRequirementDto { Type = "SourceOfFunds" };
        var expected = new RequirementResponseDto { RequirementId = Guid.NewGuid(), EngagementId = engagementId, Type = "SourceOfFunds" };

        _mockService.Setup(s => s.RequestRequirementAsync(engagementId, "tenant-001", dto)).ReturnsAsync(expected);

        // Act
        var result = await _controller.RequestRequirement(engagementId, dto, tenantId: null);

        // Assert
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        _mockService.Verify(s => s.RequestRequirementAsync(engagementId, "tenant-001", dto), Times.Once);
    }

    [Fact]
    public async Task SubmitRequirement_ExistingRequirement_ReturnsOk()
    {
        // Arrange: any authenticated caller may submit (the client, typically)
        SetupUser("tenant-001", "Client", clientId: "client-1");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var dto = new SubmitRequirementDto { Value = "Salary" };
        var expected = new RequirementResponseDto { RequirementId = requirementId, EngagementId = engagementId };

        _mockService.Setup(s => s.SubmitRequirementAsync(engagementId, requirementId, "tenant-001", dto, "client-1")).ReturnsAsync(expected);

        // Act
        var result = await _controller.SubmitRequirement(engagementId, requirementId, dto, tenantId: null);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitRequirement_NotFound_Returns404()
    {
        // Arrange
        SetupUser("tenant-001", "Client", clientId: "client-1");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var dto = new SubmitRequirementDto { Value = "Salary" };

        _mockService.Setup(s => s.SubmitRequirementAsync(engagementId, requirementId, "tenant-001", dto, "client-1"))
            .ReturnsAsync((RequirementResponseDto?)null);

        // Act
        var result = await _controller.SubmitRequirement(engagementId, requirementId, dto, tenantId: null);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitRequirement_ClientRoleWithoutResolvableIdentity_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection)
        SetupUser("tenant-001", "Client");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var dto = new SubmitRequirementDto { Value = "Salary" };

        // Act
        var result = await _controller.SubmitRequirement(engagementId, requirementId, dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(
            s => s.SubmitRequirementAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<SubmitRequirementDto>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitRequirement_StaffCaller_NoOwnershipCheckApplied()
    {
        // Arrange: Owner/Staff act across the tenant — no client-ownership constraint applies
        SetupUser("tenant-001", "Staff");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var dto = new SubmitRequirementDto { Value = "Salary" };
        var expected = new RequirementResponseDto { RequirementId = requirementId, EngagementId = engagementId };

        _mockService.Setup(s => s.SubmitRequirementAsync(engagementId, requirementId, "tenant-001", dto, null)).ReturnsAsync(expected);

        // Act
        var result = await _controller.SubmitRequirement(engagementId, requirementId, dto, tenantId: null);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(s => s.SubmitRequirementAsync(engagementId, requirementId, "tenant-001", dto, null), Times.Once);
    }

    [Fact]
    public async Task ReviewRequirement_ClientRole_Returns403Forbidden()
    {
        // Arrange
        SetupUser("tenant-001", "Client");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();

        // Act
        var result = await _controller.ReviewRequirement(
            engagementId, requirementId,
            new ReviewRequirementDto { Status = RequirementReviewStatus.Approved, ReviewerActor = "x" },
            tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.ReviewRequirementAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ReviewRequirementDto>()), Times.Never);
    }

    [Fact]
    public async Task ReviewRequirement_OwnerRole_CallsServiceAndReturnsOk()
    {
        // Arrange
        SetupUser("tenant-001", "Owner");
        var engagementId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var dto = new ReviewRequirementDto { Status = RequirementReviewStatus.Rejected, ReviewerActor = "owner-1", RejectionReason = "Incomplete" };
        var expected = new RequirementResponseDto { RequirementId = requirementId, EngagementId = engagementId, Status = "Rejected" };

        _mockService.Setup(s => s.ReviewRequirementAsync(engagementId, requirementId, "tenant-001", dto)).ReturnsAsync(expected);

        // Act
        var result = await _controller.ReviewRequirement(engagementId, requirementId, dto, tenantId: null);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(s => s.ReviewRequirementAsync(engagementId, requirementId, "tenant-001", dto), Times.Once);
    }
}
