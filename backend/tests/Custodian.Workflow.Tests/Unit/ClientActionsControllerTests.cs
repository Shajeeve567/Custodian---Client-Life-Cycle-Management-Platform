using System.Security.Claims;
using Custodian.Shared.Contracts;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
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

    [Fact]
    public async Task GetActionHistory_ValidTenantHeader_NoExplicitViewRequest_DefaultsToClientView()
    {
        // Arrange: no role claim at all — CSTD-12 fix means the safe client view is the default
        // when the staff view was never explicitly requested (previously this incorrectly
        // defaulted to the staff view for any non-Client caller).
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var expectedActions = new List<ClientActionResponseDto>
        {
            new ClientActionResponseDto { ActionId = Guid.NewGuid(), Title = "Upload Document", Status = "Pending" }
        };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, "tenant-001", true, null))
                    .ReturnsAsync(expectedActions);

        // Act: no isClientView specified
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var list = Assert.IsAssignableFrom<IEnumerable<ClientActionResponseDto>>(okResult.Value);
        Assert.Single(list);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, "tenant-001", true, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_NoRecognizedRoleExplicitlyRequestsStaffView_Returns403Forbidden()
    {
        // Arrange (CSTD-12 fix — Internal Field Leak): a caller with no Owner/Staff/Client role
        // explicitly asks for the staff view via ?isClientView=false. Must be rejected outright,
        // not silently downgraded and not served — this is the exact reported vulnerability.
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: false);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
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

    [Fact]
    public async Task CompleteAction_RequirementBackedAction_Returns400BadRequest()
    {
        // Arrange: CSTD-16 — the service throws ArgumentException for a Requirement-backed
        // action; the controller must surface this as 400, not let it bubble into a 500.
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new CompleteClientActionDto { CompletedByActor = "client-user-1" };

        _mockService.Setup(s => s.CompleteActionAsync(engagementId, actionId, "tenant-001", dto))
                    .ThrowsAsync(new ArgumentException("This action represents a Requirement and must be submitted via the Requirements API."));

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task UploadEvidence_ValidRequest_Returns200OK()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new UploadActionEvidenceDto { UploaderActor = "client-user-1", DocumentId = Guid.NewGuid() };
        var expectedResponse = new ClientActionResponseDto { ActionId = actionId, Status = ClientActionStatus.Uploaded };

        _mockService.Setup(s => s.UploadEvidenceAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.UploadEvidence(engagementId, actionId, dto, tenantId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var response = Assert.IsType<ClientActionResponseDto>(okResult.Value);
        Assert.Equal(ClientActionStatus.Uploaded, response.Status);
    }

    [Fact]
    public async Task ReviewAction_ValidRequest_Returns200OK()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ReviewActionDto { Status = ClientActionStatus.Completed, ReviewerActor = "staff-reviewer" };
        var expectedResponse = new ClientActionResponseDto { ActionId = actionId, Status = ClientActionStatus.Completed };

        _mockService.Setup(s => s.ReviewActionAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.ReviewAction(engagementId, actionId, dto, tenantId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var response = Assert.IsType<ClientActionResponseDto>(okResult.Value);
        Assert.Equal(ClientActionStatus.Completed, response.Status);
    }

    [Fact]
    public async Task ApplyVerification_ValidVerified_Returns200OK()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "compliance-lead-1"
        };
        var expectedResponse = new ClientActionResponseDto
        {
            ActionId = actionId,
            Status = ClientActionStatus.Completed,
            VerificationStatus = DocumentVerificationStatus.Verified
        };

        _mockService.Setup(s => s.ApplyVerificationOutcomeAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        var response = Assert.IsType<ClientActionResponseDto>(okResult.Value);
        Assert.Equal(ClientActionStatus.Completed, response.Status);
        Assert.Equal(DocumentVerificationStatus.Verified, response.VerificationStatus);
    }

    [Fact]
    public async Task ApplyVerification_NotFound_Returns404()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "compliance-lead-1"
        };

        _mockService.Setup(s => s.ApplyVerificationOutcomeAsync(engagementId, actionId, "tenant-001", dto))
                    .ReturnsAsync((ClientActionResponseDto?)null);

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task ApplyVerification_MissingTenant_Returns400BadRequest()
    {
        // Arrange (No header, no query, no claim)
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "compliance-lead-1"
        };

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task ApplyVerification_ServiceThrowsArgumentException_Returns400BadRequest()
    {
        // Arrange
        SetupTenantHeader("tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = "InvalidStatus",
            VerifiedBy = "compliance-lead-1"
        };

        _mockService.Setup(s => s.ApplyVerificationOutcomeAsync(engagementId, actionId, "tenant-001", dto))
                    .ThrowsAsync(new ArgumentException("Invalid verification status."));

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    // ==========================================
    // TENANT ISOLATION TESTS (CSTD-12 & CSTD-269)
    // ==========================================

    [Fact]
    public async Task GetActionHistory_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: "tenant-ATTACKER", status: null, isClientView: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task GetActionHistory_WithoutTenantQuery_UsesJwtClaimAndDefaultsToClientView()
    {
        // Arrange: JWT carries only a tenant claim, no role — safe default is client view
        // (CSTD-12 fix; previously this caller could reach the staff view by passing
        // isClientView=false despite having no recognized role).
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var actions = new List<ClientActionResponseDto>
        {
            new ClientActionResponseDto { ActionId = Guid.NewGuid(), Title = "Upload Document", Status = "Pending" }
        };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, "tenant-AUTHENTICATED", true, null))
                    .ReturnsAsync(actions);

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, "tenant-AUTHENTICATED", true, null), Times.Once);
    }

    [Fact]
    public async Task CreateAction_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var dto = new CreateClientActionDto
        {
            Title = "Test Action",
            Type = "DocumentUpload",
            Source = "Step1"
        };

        // Act
        var result = await _controller.CreateAction(engagementId, dto, tenantId: "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.CreateActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CreateClientActionDto>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAction_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new CompleteClientActionDto
        {
            CompletedByActor = "staff-user-1"
        };

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.CompleteActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CompleteClientActionDto>()), Times.Never);
    }

    [Fact]
    public async Task UploadEvidence_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new UploadActionEvidenceDto
        {
            DocumentId = Guid.NewGuid(),
            UploaderActor = "client-user-1"
        };

        // Act
        var result = await _controller.UploadEvidence(engagementId, actionId, dto, tenantId: "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.UploadEvidenceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UploadActionEvidenceDto>()), Times.Never);
    }

    [Fact]
    public async Task ReviewAction_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ReviewActionDto
        {
            Status = ClientActionStatus.Completed,
            ReviewerActor = "staff-user-1"
        };

        // Act
        var result = await _controller.ReviewAction(engagementId, actionId, dto, tenantId: "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.ReviewActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ReviewActionDto>()), Times.Never);
    }

    [Fact]
    public async Task ApplyVerification_MismatchedTenantQuery_Returns403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "staff-lead"
        };

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.ApplyVerificationOutcomeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ApplyActionVerificationDto>()), Times.Never);
    }

    [Fact]
    public async Task GetActionHistory_ClientRoleRequestsClientViewFalse_ForcesClientViewTrue()
    {
        // Arrange (CSTD-12 Group B): Client passes ?isClientView=false to attempt viewing internal actions
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, tenantId, true, null))
            .ReturnsAsync(new List<ClientActionResponseDto>());

        // Act: isClientView is explicitly set to false in query
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: false);

        // Assert: Controller MUST enforce isClientView: true when calling service
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, true, null), Times.Once);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null), Times.Never);
    }

    [Fact]
    public async Task GetActionHistory_StaffRoleRequestsClientViewFalse_AllowsStaffView()
    {
        // Arrange: Staff requests staff view (isClientView: false)
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Staff")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null))
            .ReturnsAsync(new List<ClientActionResponseDto>());

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: false);

        // Assert: Staff is permitted staff view
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_OwnerRoleRequestsClientViewFalse_AllowsStaffView()
    {
        // Arrange: Owner requests staff view (isClientView: false) — the story names both
        // Staff and Owner as authorized for the staff view.
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Owner")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null))
            .ReturnsAsync(new List<ClientActionResponseDto>());

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: false);

        // Assert: Owner is permitted staff view
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_ClientRoleRequestsViaXClientViewHeader_ForcesClientViewTrue()
    {
        // Arrange (CSTD-12): the X-Client-View header must not be a bypass route either —
        // a Client-role caller setting X-Client-View: false must still be forced to client view.
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.Request.Headers["X-Client-View"] = "false";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.GetActionsByEngagementAsync(engagementId, tenantId, true, null))
            .ReturnsAsync(new List<ClientActionResponseDto>());

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, true, null), Times.Once);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(engagementId, tenantId, false, null), Times.Never);
    }

    [Fact]
    public async Task GetActionHistory_NoRecognizedRoleUsesXClientViewHeaderToRequestStaffView_Returns403Forbidden()
    {
        // Arrange (CSTD-12): the X-Client-View header is an equally valid attack route as the
        // query parameter — must be rejected the same way for a caller with no Owner/Staff role.
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.Request.Headers["X-Client-View"] = "false";

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ReviewAction_ClientRole_Returns403Forbidden()
    {
        // Arrange: Authenticated user in Client role attempts to review/approve an action
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var dto = new ReviewActionDto { Status = ClientActionStatus.Completed };

        // Act
        var result = await _controller.ReviewAction(engagementId, actionId, dto, tenantId: null);

        // Assert: 403 Forbidden
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.ReviewActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ReviewActionDto>()), Times.Never);
    }

    [Fact]
    public async Task ApplyVerification_ClientRole_Returns403Forbidden()
    {
        // Arrange: Authenticated user in Client role attempts to apply verification outcome
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "client-attempt"
        };

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert: 403 Forbidden
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.ApplyVerificationOutcomeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ApplyActionVerificationDto>()), Times.Never);
    }

    [Fact]
    public async Task ReviewAction_StaffRole_Returns200OK()
    {
        // Arrange: Staff role reviewing an action
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Staff"),
            new Claim("sub", "staff-user-01")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var dto = new ReviewActionDto { Status = ClientActionStatus.Completed };
        var expectedResponse = new ClientActionResponseDto { ActionId = actionId, Status = ClientActionStatus.Completed };

        _mockService.Setup(s => s.ReviewActionAsync(engagementId, actionId, tenantId, dto))
                    .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.ReviewAction(engagementId, actionId, dto, tenantId: null);

        // Assert: 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
    }

    [Fact]
    public async Task ApplyVerification_StaffRole_Returns200OK()
    {
        // Arrange: Staff role applying verification
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, "Staff"),
            new Claim("sub", "staff-user-01")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified
        };
        var expectedResponse = new ClientActionResponseDto { ActionId = actionId, Status = ClientActionStatus.Completed };

        _mockService.Setup(s => s.ApplyVerificationOutcomeAsync(engagementId, actionId, tenantId, dto))
                    .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.ApplyVerification(engagementId, actionId, dto, tenantId: null);

        // Assert: 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, okResult.StatusCode);
    }
}
