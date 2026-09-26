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
    private readonly Mock<IStallDetectionService> _mockStall;
    private readonly Mock<IStallActionsProvider> _mockStallActions;
    private readonly Mock<IStallEventDeduplicator> _mockStallDedup;
    private readonly Mock<IAuditPublisher> _mockAuditPublisher;
    private readonly Mock<ILogger<ClientActionsController>> _mockLogger;
    private readonly ClientActionsController _controller;

    public ClientActionsControllerTests()
    {
        _mockService = new Mock<IClientActionService>();
        _mockStall = new Mock<IStallDetectionService>();
        _mockStallActions = new Mock<IStallActionsProvider>();
        _mockStallDedup = new Mock<IStallEventDeduplicator>();
        _mockAuditPublisher = new Mock<IAuditPublisher>();
        _mockLogger = new Mock<ILogger<ClientActionsController>>();

        _controller = new ClientActionsController(
            _mockService.Object,
            _mockStall.Object,
            _mockStallActions.Object,
            _mockStallDedup.Object,
            _mockAuditPublisher.Object,
            _mockLogger.Object);
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
    public async Task GetActionHistory_ClientRoleNotOwningEngagement_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection, extended to ClientActionsController): tenant matches,
        // but the engagement belongs to a different client.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-attacker")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var engagementId = Guid.NewGuid();
        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-attacker")).ReturnsAsync(false);

        // Act
        var result = await _controller.GetActionHistory(engagementId, tenantId: null, status: null, isClientView: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetActionHistory_ClientRoleWithoutResolvableIdentity_Returns403Forbidden()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await _controller.GetActionHistory(Guid.NewGuid(), tenantId: null, status: null, isClientView: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.GetActionsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
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
    public async Task CreateAction_ClientRoleNotOwningEngagement_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection, extended to ClientActionsController)
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-attacker")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var engagementId = Guid.NewGuid();
        var dto = new CreateClientActionDto { Title = "Forged task", Type = "CustomTask", Source = "Attacker" };

        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-attacker")).ReturnsAsync(false);

        // Act
        var result = await _controller.CreateAction(engagementId, dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.CreateActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CreateClientActionDto>()), Times.Never);
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
    public async Task CompleteAction_ClientRoleNotOwningEngagement_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection, extended to ClientActionsController)
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-attacker")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new CompleteClientActionDto { CompletedByActor = "client-attacker" };

        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-attacker")).ReturnsAsync(false);

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.CompleteActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CompleteClientActionDto>()), Times.Never);
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
    public async Task UploadEvidence_ClientRoleNotOwningEngagement_Returns403Forbidden()
    {
        // Arrange (CSTD-22 IDOR protection, extended to ClientActionsController): a Client-role
        // caller whose own client_id doesn't match the engagement's owner must be denied outright.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-attacker")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var dto = new UploadActionEvidenceDto { UploaderActor = "client-attacker" };

        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-attacker")).ReturnsAsync(false);

        // Act
        var result = await _controller.UploadEvidence(engagementId, actionId, dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.UploadEvidenceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UploadActionEvidenceDto>()), Times.Never);
    }

    [Fact]
    public async Task UploadEvidence_ClientRoleWithoutResolvableIdentity_Returns403Forbidden()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-001";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-001"),
            new Claim(ClaimTypes.Role, "Client")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var dto = new UploadActionEvidenceDto { UploaderActor = "client-user-1" };

        // Act
        var result = await _controller.UploadEvidence(Guid.NewGuid(), Guid.NewGuid(), dto, tenantId: null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.UploadEvidenceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UploadActionEvidenceDto>()), Times.Never);
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
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-1")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, tenantId, "client-1")).ReturnsAsync(true);
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
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", "client-1")
        }, "TestAuth"));

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, tenantId, "client-1")).ReturnsAsync(true);
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

    [Fact]
    public async Task CreateAction_InvalidOperationException_Returns409Conflict()
    {
        // Arrange
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        SetupTenantHeader(tenantId);

        var dto = new CreateClientActionDto
        {
            Title = "Submit Tax Return",
            Type = ClientActionType.DocumentUpload
        };

        _mockService.Setup(s => s.CreateActionAsync(engagementId, tenantId, dto))
            .ThrowsAsync(new InvalidOperationException("Cannot create action for an engagement with status 'Closed'."));

        // Act
        var result = await _controller.CreateAction(engagementId, dto, tenantId: null);

        // Assert: 409 Conflict
        var conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflictResult.StatusCode);
    }

    [Fact]
    public async Task CompleteAction_InvalidOperationException_Returns409Conflict()
    {
        // Arrange
        var tenantId = "tenant-001";
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        SetupTenantHeader(tenantId);

        var dto = new CompleteClientActionDto
        {
            CompletedByActor = "StaffMember"
        };

        _mockService.Setup(s => s.CompleteActionAsync(engagementId, actionId, tenantId, dto))
            .ThrowsAsync(new InvalidOperationException("Cannot transition from Cancelled to Completed."));

        // Act
        var result = await _controller.CompleteAction(engagementId, actionId, dto, tenantId: null);

        // Assert: 409 Conflict
        var conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflictResult.StatusCode);
    }

    // =========================================================================
    // Staff-defined stage tasks: create/edit/cancel/checklist are Owner/Staff only
    // =========================================================================

    private void SetupRole(string role, string tenantId = "tenant-001", string? clientId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.NameIdentifier, $"{role.ToLowerInvariant()}-user-1")
        };
        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
        httpContext.Request.Headers["X-Tenant-ID"] = tenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task CreateAction_ClientOwningEngagement_Returns403_ClientsCannotDefineTasks()
    {
        SetupRole("Client", clientId: "client-owner");
        var engagementId = Guid.NewGuid();
        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-owner")).ReturnsAsync(true);

        var result = await _controller.CreateAction(engagementId, new CreateClientActionDto { Title = "Self task", Type = "CustomTask", Source = "Client" }, tenantId: null);

        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(s => s.CreateActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CreateClientActionDto>()), Times.Never);
    }

    [Fact]
    public async Task CreateAction_UnknownEngagement_Returns404()
    {
        SetupRole("Staff");
        var engagementId = Guid.NewGuid();
        _mockService.Setup(s => s.CreateActionAsync(engagementId, "tenant-001", It.IsAny<CreateClientActionDto>()))
            .ThrowsAsync(new KeyNotFoundException("Engagement not found."));

        var result = await _controller.CreateAction(engagementId, new CreateClientActionDto { Title = "Task", Type = "CustomTask", Source = "StaffManual" }, tenantId: null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateAction_PastStage_Returns400()
    {
        SetupRole("Staff");
        var engagementId = Guid.NewGuid();
        _mockService.Setup(s => s.CreateActionAsync(engagementId, "tenant-001", It.IsAny<CreateClientActionDto>()))
            .ThrowsAsync(new ArgumentException("Tasks can only be added to the current stage (3) or a later one."));

        var result = await _controller.CreateAction(engagementId, new CreateClientActionDto { Title = "Task", Type = "CustomTask", Source = "StaffManual", StageNumber = 1 }, tenantId: null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ApplyStandardChecklist_Staff_Returns200_WithActorFromJwt()
    {
        SetupRole("Staff");
        var engagementId = Guid.NewGuid();
        _mockService.Setup(s => s.ApplyStandardChecklistAsync(engagementId, "tenant-001", "staff-user-1"))
            .ReturnsAsync(new List<ClientActionResponseDto> { new() { Title = "Client Intake" } });

        var result = await _controller.ApplyStandardChecklist(engagementId, tenantId: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientActionResponseDto>>(ok.Value));
    }

    [Fact]
    public async Task ApplyStandardChecklist_UnknownEngagement_Returns404_Closed_Returns409()
    {
        SetupRole("Owner");
        var missing = Guid.NewGuid();
        var closed = Guid.NewGuid();
        _mockService.Setup(s => s.ApplyStandardChecklistAsync(missing, "tenant-001", It.IsAny<string>()))
            .ReturnsAsync((List<ClientActionResponseDto>?)null);
        _mockService.Setup(s => s.ApplyStandardChecklistAsync(closed, "tenant-001", It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("closed"));

        Assert.IsType<NotFoundObjectResult>((await _controller.ApplyStandardChecklist(missing, tenantId: null)).Result);
        Assert.IsType<ConflictObjectResult>((await _controller.ApplyStandardChecklist(closed, tenantId: null)).Result);
    }

    [Fact]
    public async Task UpdateAction_Staff_Returns200_NotFound404_NotPending409()
    {
        SetupRole("Staff");
        var engagementId = Guid.NewGuid();
        var ok = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var dto = new UpdateClientActionDto { Title = "Renamed" };
        _mockService.Setup(s => s.UpdateActionAsync(engagementId, ok, "tenant-001", dto, "staff-user-1"))
            .ReturnsAsync(new ClientActionResponseDto { ActionId = ok, Title = "Renamed" });
        _mockService.Setup(s => s.UpdateActionAsync(engagementId, missing, "tenant-001", dto, It.IsAny<string>()))
            .ReturnsAsync((ClientActionResponseDto?)null);
        _mockService.Setup(s => s.UpdateActionAsync(engagementId, completed, "tenant-001", dto, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Only Pending tasks can be edited."));

        Assert.IsType<OkObjectResult>((await _controller.UpdateAction(engagementId, ok, dto, tenantId: null)).Result);
        Assert.IsType<NotFoundObjectResult>((await _controller.UpdateAction(engagementId, missing, dto, tenantId: null)).Result);
        Assert.IsType<ConflictObjectResult>((await _controller.UpdateAction(engagementId, completed, dto, tenantId: null)).Result);
    }

    [Fact]
    public async Task CancelAction_Staff_Returns200_CompletedTask409()
    {
        SetupRole("Owner");
        var engagementId = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var completed = Guid.NewGuid();
        _mockService.Setup(s => s.CancelActionAsync(engagementId, pending, "tenant-001", "No longer required", "owner-user-1"))
            .ReturnsAsync(new ClientActionResponseDto { ActionId = pending, Status = ClientActionStatus.Cancelled });
        _mockService.Setup(s => s.CancelActionAsync(engagementId, completed, "tenant-001", It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Completed is terminal."));

        var dto = new CancelClientActionDto { Reason = "No longer required" };
        Assert.IsType<OkObjectResult>((await _controller.CancelAction(engagementId, pending, dto, tenantId: null)).Result);
        Assert.IsType<ConflictObjectResult>((await _controller.CancelAction(engagementId, completed, dto, tenantId: null)).Result);
    }

    [Fact]
    public async Task StaffTaskEndpoints_ClientRole_Return403_AndNeverCallService()
    {
        SetupRole("Client", clientId: "client-owner");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        _mockService.Setup(s => s.ClientOwnsEngagementAsync(engagementId, "tenant-001", "client-owner")).ReturnsAsync(true);

        Assert.IsType<ForbidResult>((await _controller.ApplyStandardChecklist(engagementId, tenantId: null)).Result);
        Assert.IsType<ForbidResult>((await _controller.UpdateAction(engagementId, actionId, new UpdateClientActionDto { Title = "x" }, tenantId: null)).Result);
        Assert.IsType<ForbidResult>((await _controller.CancelAction(engagementId, actionId, new CancelClientActionDto { Reason = "x" }, tenantId: null)).Result);

        _mockService.Verify(s => s.ApplyStandardChecklistAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockService.Verify(s => s.UpdateActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpdateClientActionDto>(), It.IsAny<string>()), Times.Never);
        _mockService.Verify(s => s.CancelActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StaffTaskEndpoints_CrossTenantQuery_Return403()
    {
        SetupRole("Staff", tenantId: "tenant-001");
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        Assert.IsType<ForbidResult>((await _controller.ApplyStandardChecklist(engagementId, tenantId: "tenant-other")).Result);
        Assert.IsType<ForbidResult>((await _controller.UpdateAction(engagementId, actionId, new UpdateClientActionDto { Title = "x" }, tenantId: "tenant-other")).Result);
        Assert.IsType<ForbidResult>((await _controller.CancelAction(engagementId, actionId, new CancelClientActionDto { Reason = "x" }, tenantId: "tenant-other")).Result);
    }
}
