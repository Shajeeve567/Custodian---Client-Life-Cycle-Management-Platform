using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for EngagementsController.
/// We mock IEngagementRepository and HttpContext to test controller HTTP responses, status codes, and JWT claim tenant isolation!
/// </summary>
public class EngagementsControllerUnitTests
{
    private readonly Mock<IEngagementRepository> _mockRepo;
    private readonly Mock<IAuditPublisher> _mockAuditPublisher;
    private readonly Mock<IGateEvaluator> _mockGateEvaluator;
    private readonly Mock<INextActionService> _mockNextActionService;
    private readonly EngagementsController _controller;

    public EngagementsControllerUnitTests()
    {
        _mockRepo = new Mock<IEngagementRepository>();
        _mockAuditPublisher = new Mock<IAuditPublisher>();
        _mockGateEvaluator = new Mock<IGateEvaluator>();
        _mockNextActionService = new Mock<INextActionService>();

        // Default: gates are satisfied unless a specific test overrides this, so the
        // existing transition/tenant-isolation tests are unaffected by the CSTD-18
        // gate-check addition to UpdateStage.
        _mockGateEvaluator
            .Setup(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        _controller = new EngagementsController(
            _mockRepo.Object,
            _mockAuditPublisher.Object,
            _mockGateEvaluator.Object,
            actionService: null,
            nextActionService: _mockNextActionService.Object);
    }

    /// <summary>
    /// Helper method to simulate an authenticated HTTP request with specific JWT claims (e.g. tenant_id)
    /// </summary>
    private void SetupUserJwtClaim(string tenantIdClaim, string role = "Staff")
    {
        var claims = new List<Claim>
        {
            new Claim("tenant_id", tenantIdClaim),
            new Claim(ClaimTypes.Role, role)
        };

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    // ==========================================
    // 1. CREATE ENGAGEMENT TESTS
    // ==========================================

    [Fact]
    public async Task CreateEngagement_ValidRequest_ShouldReturn201Created()
    {
        // Arrange
        var request = new CreateEngagementRequest
        {
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001"
        };

        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Engagement>()))
                 .ReturnsAsync((Engagement e) => e);

        // Act
        var actionResult = await _controller.CreateEngagement(request);

        // Assert: Expect 201 Created with EngagementResponse
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        Assert.Equal(201, createdResult.StatusCode);

        var response = Assert.IsType<EngagementResponse>(createdResult.Value);
        Assert.Equal("tenant-001", response.TenantId);
        Assert.Equal("client-001", response.ClientId);
        Assert.Equal("Draft", response.Status);
    }

    [Fact]
    public async Task CreateEngagement_ValidRequest_ShouldPublishGenesisAuditEvent()
    {
        // Arrange
        var request = new CreateEngagementRequest
        {
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001"
        };

        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Engagement>()))
                 .ReturnsAsync((Engagement e) => e);

        // Act
        await _controller.CreateEngagement(request);

        // Assert: Verify PublishGenesisEventAsync was invoked with Genesis event
        _mockAuditPublisher.Verify(a => a.PublishGenesisEventAsync(
            It.IsAny<Engagement>(),
            "tenant-001"
        ), Times.Once);
    }

    [Fact]
    public async Task CreateEngagement_MissingTenantId_ShouldReturn400BadRequest()
    {
        // Arrange: Payload missing tenant ID and no JWT claim provided
        var request = new CreateEngagementRequest
        {
            TenantId = "",
            ClientId = "client-001",
            StaffId = "staff-001"
        };

        // Act
        var actionResult = await _controller.CreateEngagement(request);

        // Assert: Expect 400 Bad Request
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task CreateEngagement_ValidRequest_ShouldDefaultToInitialStage()
    {
        // Arrange
        var request = new CreateEngagementRequest
        {
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001"
        };

        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Engagement>()))
                 .ReturnsAsync((Engagement e) => e);

        // Act
        var actionResult = await _controller.CreateEngagement(request);

        // Assert: New engagements start at the first pipeline stage with 0% progress
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        var response = Assert.IsType<EngagementResponse>(createdResult.Value);
        Assert.Equal("Onboarding", response.Stage);
        Assert.Equal(0, response.StageProgressPercentage);
    }

    [Fact]
    public async Task CreateEngagement_ValidRequest_ShouldIncludeStageInGenesisPayload()
    {
        // Arrange
        var request = new CreateEngagementRequest
        {
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001"
        };

        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Engagement>()))
                 .ReturnsAsync((Engagement e) => e);

        // Act
        await _controller.CreateEngagement(request);

        // Assert: The Genesis audit event is published with the initial stage on the engagement
        _mockAuditPublisher.Verify(a => a.PublishGenesisEventAsync(
            It.Is<Engagement>(e => e.Stage == EngagementStage.Onboarding),
            "tenant-001"
        ), Times.Once);
    }

    // ==========================================
    // 2. GET ENGAGEMENT BY ID TESTS
    // ==========================================

    [Fact]
    public async Task GetEngagementById_ExistingId_ShouldReturn200OK()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Draft
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001"))
                 .ReturnsAsync(engagement);

        // Act
        var actionResult = await _controller.GetEngagementById(engagementId, "tenant-001");

        // Assert: Expect 200 OK
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(200, okResult.StatusCode);

        var response = Assert.IsType<EngagementResponse>(okResult.Value);
        Assert.Equal(engagementId, response.EngagementId);
        Assert.Equal("Onboarding", response.Stage);
        Assert.Equal(0, response.StageProgressPercentage);
    }

    [Fact]
    public async Task GetEngagementById_NonExistentOrCrossTenant_ShouldReturn404NotFound()
    {
        // Arrange: Repository returns null when tenant does not match
        var engagementId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-attacker"))
                 .ReturnsAsync((Engagement?)null);

        // Act
        var actionResult = await _controller.GetEngagementById(engagementId, "tenant-attacker");

        // Assert: Expect 404 Not Found to avoid leaking entity existence across tenants
        var notFound = Assert.IsType<NotFoundResult>(actionResult.Result);
        Assert.Equal(404, notFound.StatusCode);
    }

    // ==========================================
    // 3. GET ALL ENGAGEMENTS TESTS
    // ==========================================

    [Fact]
    public async Task GetEngagements_ValidTenant_ShouldReturn200OKWithList()
    {
        // Arrange
        var engagements = new List<Engagement>
        {
            new Engagement { EngagementId = Guid.NewGuid(), TenantId = "tenant-001", ClientId = "c1", StaffId = "s1" },
            new Engagement { EngagementId = Guid.NewGuid(), TenantId = "tenant-001", ClientId = "c2", StaffId = "s2" }
        };

        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-001"))
                 .ReturnsAsync(engagements);

        // Act
        var actionResult = await _controller.GetEngagements("tenant-001");

        // Assert: Expect 200 OK with 2 items
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var list = Assert.IsAssignableFrom<IEnumerable<EngagementResponse>>(okResult.Value);
        Assert.Equal(2, list.Count());
    }

    // ==========================================
    // 4. UPDATE STATUS & LIFECYCLE VALIDATION TESTS
    // ==========================================

    [Fact]
    public async Task UpdateStatus_ValidTransition_ShouldReturn200OK()
    {
        // Arrange: Existing Draft engagement
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "c1",
            StaffId = "s1",
            Status = EngagementStatus.Draft
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Engagement>())).ReturnsAsync(engagement);

        var request = new UpdateEngagementStatusRequest { TenantId = "tenant-001", Status = "Started" };

        // Act: Transition Draft -> Started
        var actionResult = await _controller.UpdateStatus(engagementId, request);

        // Assert: Expect 200 OK
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<EngagementResponse>(okResult.Value);
        Assert.Equal("Started", response.Status);
    }

    [Fact]
    public async Task UpdateStatus_IllegalTransition_ShouldReturn400BadRequest()
    {
        // Arrange: Closed engagement cannot be demoted to Draft
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "c1",
            StaffId = "s1",
            Status = EngagementStatus.Closed
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStatusRequest { TenantId = "tenant-001", Status = "Draft" };

        // Act: Attempt illegal transition Closed -> Draft
        var actionResult = await _controller.UpdateStatus(engagementId, request);

        // Assert: Expect 400 Bad Request with lifecycle error explanation
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    // ==========================================
    // 5. DELETE ENGAGEMENT & LIFECYCLE PROTECTION TESTS
    // ==========================================

    [Fact]
    public async Task DeleteEngagement_DraftStatus_ShouldReturn24NoContent()
    {
        // Arrange: Draft engagement CAN be physically deleted
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            Status = EngagementStatus.Draft
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockRepo.Setup(r => r.DeleteAsync(engagementId, "tenant-001")).ReturnsAsync(true);

        // Act
        var actionResult = await _controller.DeleteEngagement(engagementId, "tenant-001");

        // Assert: Expect 204 No Content
        var noContent = Assert.IsType<NoContentResult>(actionResult);
        Assert.Equal(204, noContent.StatusCode);
    }

    [Theory]
    [InlineData(EngagementStatus.Started)]
    [InlineData(EngagementStatus.Closed)]
    public async Task DeleteEngagement_StartedOrClosedStatus_ShouldReturn409Conflict(EngagementStatus status)
    {
        // Arrange: Started and Closed engagements CANNOT be physically deleted
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            Status = status
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        // Act: Attempt to delete non-Draft engagement
        var actionResult = await _controller.DeleteEngagement(engagementId, "tenant-001");

        // Assert: Expect 409 Conflict per Custodian lifecycle protection rules
        var conflictResult = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Equal(409, conflictResult.StatusCode);
    }

    // ==========================================
    // 6. UPDATE STAGE & PROGRESS TESTS (CSTD-17)
    // ==========================================

    private static Engagement MakeEngagement(Guid id, EngagementStatus status, EngagementStage stage) => new()
    {
        EngagementId = id,
        TenantId = "tenant-001",
        ClientId = "c1",
        StaffId = "s1",
        Status = status,
        Stage = stage
    };

    [Fact]
    public async Task UpdateStage_ValidSequentialTransition_ShouldReturn200OK()
    {
        // Arrange: Engagement at the first stage
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.Onboarding);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Engagement>())).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "DocumentCollection" };

        // Act: Advance one stage forward
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 200 OK with updated stage and derived progress
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<EngagementResponse>(okResult.Value);
        Assert.Equal("DocumentCollection", response.Stage);
        Assert.Equal(25, response.StageProgressPercentage);
    }

    [Fact]
    public async Task UpdateStage_SkipAhead_ShouldReturn400BadRequest()
    {
        // Arrange: Attempt to skip from stage 1 directly to stage 3
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.Onboarding);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Verification" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 400 Bad Request, skipping stages is not allowed
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task UpdateStage_Backwards_ShouldReturn400BadRequest()
    {
        // Arrange: Attempt to move backwards from stage 2 to stage 1
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.DocumentCollection);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Onboarding" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 400 Bad Request, backwards transitions are not allowed
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task UpdateStage_FromTerminalStage_ShouldReturn400BadRequest()
    {
        // Arrange: Engagement already at the final stage
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.Closure);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Onboarding" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 400 Bad Request, the final stage is terminal
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task UpdateStage_OnClosedOrCancelledEngagement_ShouldReturn409Conflict(EngagementStatus status)
    {
        // Arrange: Engagement whose Status is terminal
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, status, EngagementStage.Onboarding);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "DocumentCollection" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 409 Conflict, a Closed/Cancelled engagement's stage cannot move
        var conflictResult = Assert.IsType<ConflictObjectResult>(actionResult.Result);
        Assert.Equal(409, conflictResult.StatusCode);
    }

    [Fact]
    public async Task UpdateStage_InvalidStageName_ShouldReturn400BadRequest()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.Onboarding);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "NotARealStage" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: Expect 400 Bad Request for an unrecognized stage name
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task UpdateStage_ValidTransition_ShouldPublishStageChangeAuditEvent()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.Onboarding);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Engagement>())).ReturnsAsync(engagement);

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "DocumentCollection" };

        // Act
        await _controller.UpdateStage(engagementId, request);

        // Assert: A StageChange audit event is published for every successful transition
        _mockAuditPublisher.Verify(a => a.PublishEventAsync(
            engagementId,
            "tenant-001",
            It.IsAny<string>(),
            "StageChange",
            It.IsAny<object>()
        ), Times.Once);
    }

    // ==========================================
    // 7. GATE EVALUATION TESTS (CSTD-18)
    // ==========================================

    [Fact]
    public async Task UpdateStage_GateBlocked_ShouldReturn400BadRequestWithReason()
    {
        // Arrange: transition is otherwise legal, but the gate evaluator blocks it
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.DocumentCollection);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);

        const string blockReason = "Required document 'KYC_PASSPORT' has not been submitted.";
        _mockGateEvaluator
            .Setup(g => g.EvaluateAsync(engagementId, "tenant-001", EngagementStage.Verification, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Blocked(blockReason, new[] { new GateRequirementResult("KYC_PASSPORT", false, blockReason) }));

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Verification" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert: 400 Bad Request carrying the gate's human-readable reason
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task UpdateStage_GateBlocked_ShouldNotPersistStageOrPublishAuditEvent()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.DocumentCollection);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockGateEvaluator
            .Setup(g => g.EvaluateAsync(engagementId, "tenant-001", EngagementStage.Verification, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Blocked("Required document 'KYC_PASSPORT' has not been submitted.", Array.Empty<GateRequirementResult>()));

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Verification" };

        // Act
        await _controller.UpdateStage(engagementId, request);

        // Assert: a blocked gate must not mutate the engagement or emit a StageChange event
        _mockRepo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
        _mockAuditPublisher.Verify(a => a.PublishEventAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "StageChange", It.IsAny<object>()
        ), Times.Never);
    }

    [Fact]
    public async Task UpdateStage_GateSatisfied_ShouldReturn200OKAndProceed()
    {
        // Arrange: transition legal AND gate explicitly satisfied
        var engagementId = Guid.NewGuid();
        var engagement = MakeEngagement(engagementId, EngagementStatus.Started, EngagementStage.DocumentCollection);

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-001")).ReturnsAsync(engagement);
        _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Engagement>())).ReturnsAsync(engagement);
        _mockGateEvaluator
            .Setup(g => g.EvaluateAsync(engagementId, "tenant-001", EngagementStage.Verification, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        var request = new UpdateEngagementStageRequest { TenantId = "tenant-001", Stage = "Verification" };

        // Act
        var actionResult = await _controller.UpdateStage(engagementId, request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(200, okResult.StatusCode);
        _mockRepo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Once);
    }

    // ==========================================
    // 8. TENANT ISOLATION & QA ACCEPTANCE CRITERIA TESTS (CSTD-12 & CSTD-269)
    // ==========================================

    [Fact]
    public async Task GetEngagements_MismatchedTenantQuery_ShouldReturn403Forbidden()
    {
        // Arrange: Authenticated JWT user belongs to tenant-AUTHENTICATED
        SetupUserJwtClaim("tenant-AUTHENTICATED");

        // Act: Client attempts to access Company B's engagements: ?tenantId=tenant-ATTACKER
        var actionResult = await _controller.GetEngagements("tenant-ATTACKER");

        // Assert: Controller strictly forbids cross-tenant access with 403 Forbidden
        Assert.IsType<ForbidResult>(actionResult.Result);
        _mockRepo.Verify(r => r.GetAllByTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetEngagements_WithoutTenantQuery_ShouldUseJwtClaimAndReturn200OK()
    {
        // Arrange: Authenticated JWT user belongs to tenant-AUTHENTICATED
        SetupUserJwtClaim("tenant-AUTHENTICATED");

        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = "tenant-AUTHENTICATED",
            ClientId = "c1",
            StaffId = "s1"
        };

        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-AUTHENTICATED"))
                 .ReturnsAsync(new List<Engagement> { engagement });

        // Act: Client calls GET /api/engagements with valid JWT and no query parameter
        var actionResult = await _controller.GetEngagements(null);

        // Assert: Controller resolves caller's own tenant and returns 200 OK
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var list = Assert.IsAssignableFrom<IEnumerable<EngagementResponse>>(okResult.Value);
        Assert.Single(list);
        _mockRepo.Verify(r => r.GetAllByTenantAsync("tenant-AUTHENTICATED"), Times.Once);
    }

    [Fact]
    public async Task GetEngagementById_MismatchedTenantQuery_ShouldReturn403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");

        // Act
        var actionResult = await _controller.GetEngagementById(Guid.NewGuid(), "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(actionResult.Result);
        _mockRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetEngagementById_WithoutTenantQuery_ShouldUseJwtClaim()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-AUTHENTICATED",
            ClientId = "c1",
            StaffId = "s1"
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, "tenant-AUTHENTICATED"))
                 .ReturnsAsync(engagement);

        // Act
        var actionResult = await _controller.GetEngagementById(engagementId, null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<EngagementResponse>(okResult.Value);
        Assert.Equal(engagementId, response.EngagementId);
    }

    [Fact]
    public async Task CreateEngagement_MismatchedTenantPayload_ShouldReturn403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var request = new CreateEngagementRequest
        {
            TenantId = "tenant-ATTACKER",
            ClientId = "c1",
            StaffId = "s1"
        };

        // Act
        var actionResult = await _controller.CreateEngagement(request);

        // Assert
        Assert.IsType<ForbidResult>(actionResult.Result);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Engagement>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_MismatchedTenantPayload_ShouldReturn403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");
        var request = new UpdateEngagementStatusRequest
        {
            TenantId = "tenant-ATTACKER",
            Status = "Started"
        };

        // Act
        var actionResult = await _controller.UpdateStatus(Guid.NewGuid(), request);

        // Assert
        Assert.IsType<ForbidResult>(actionResult.Result);
        _mockRepo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
    }

    [Fact]
    public async Task DeleteEngagement_MismatchedTenantQuery_ShouldReturn403Forbidden()
    {
        // Arrange
        SetupUserJwtClaim("tenant-AUTHENTICATED");

        // Act
        var actionResult = await _controller.DeleteEngagement(Guid.NewGuid(), "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(actionResult);
        _mockRepo.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // ==========================================
    // 7. GET NEXT ACTION TESTS (CSTD-19 / 19-N2)
    // ==========================================

    [Fact]
    public async Task GetNextAction_ValidStaffContext_Returns200WithStaffViewNextActionResult()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-custodian-1";
        SetupUserJwtClaim(tenantId, "Staff");

        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        };

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, tenantId))
            .ReturnsAsync(engagement);

        var expectedResult = new NextActionResult
        {
            EngagementId = engagementId,
            EngagementStatus = "Started",
            CurrentStage = "Onboarding",
            OverallState = OverallState.ClientActionRequired,
            PrimaryAction = new NextActionItem
            {
                Kind = NextActionKind.DocumentUpload,
                ResponsibleParty = ResponsibleParty.Client,
                Title = "Upload Articles of Incorporation",
                Reason = "Document required for onboarding gate.",
                PriorityRank = 4
            },
            Blockers = new List<NextActionItem>
            {
                new NextActionItem
                {
                    Kind = NextActionKind.StaffTask,
                    ResponsibleParty = ResponsibleParty.Staff,
                    Title = "Review background check",
                    Reason = "Staff check pending",
                    PriorityRank = 11
                }
            },
            NextStageGate = new GateSummary
            {
                TargetStage = "DocumentCollection",
                IsSatisfied = false,
                Reasons = new List<string> { "Document missing" }
            },
            IsStalled = false,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        };

        _mockNextActionService
            .Setup(s => s.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var actionResult = await _controller.GetNextAction(engagementId, tenantId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(200, okResult.StatusCode);

        var resultDto = Assert.IsType<NextActionResult>(okResult.Value);
        Assert.Equal(engagementId, resultDto.EngagementId);
        Assert.Equal(OverallState.ClientActionRequired, resultDto.OverallState);
        Assert.NotNull(resultDto.PrimaryAction);
        Assert.Equal("Upload Articles of Incorporation", resultDto.PrimaryAction.Title);
        Assert.Single(resultDto.Blockers);
        Assert.NotNull(resultDto.NextStageGate);

        _mockNextActionService.Verify(
            s => s.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetNextAction_EngagementNotFound_Returns404NotFound()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-custodian-1";
        SetupUserJwtClaim(tenantId, "Staff");

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, tenantId))
            .ReturnsAsync((Engagement?)null);

        // Act
        var actionResult = await _controller.GetNextAction(engagementId, tenantId);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(actionResult.Result);
        Assert.Equal(404, notFound.StatusCode);

        _mockNextActionService.Verify(
            s => s.GetNextActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<NextActionView>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetNextAction_CrossTenantAccess_Returns403Forbidden()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        SetupUserJwtClaim("tenant-AUTHENTICATED", "Staff");

        // Act
        var actionResult = await _controller.GetNextAction(engagementId, "tenant-ATTACKER");

        // Assert
        Assert.IsType<ForbidResult>(actionResult.Result);

        _mockRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _mockNextActionService.Verify(
            s => s.GetNextActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<NextActionView>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetNextAction_MissingTenant_Returns400BadRequest()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var actionResult = await _controller.GetNextAction(engagementId, tenantId: null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal(400, badRequest.StatusCode);

        _mockRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetNextAction_EngineReturnsNull_Returns404NotFound()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-custodian-1";
        SetupUserJwtClaim(tenantId, "Staff");

        _mockRepo.Setup(r => r.GetByIdAsync(engagementId, tenantId))
            .ReturnsAsync(new Engagement
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Status = EngagementStatus.Started
            });

        _mockNextActionService
            .Setup(s => s.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NextActionResult?)null);

        // Act
        var actionResult = await _controller.GetNextAction(engagementId, tenantId);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(actionResult.Result);
        Assert.Equal(404, notFound.StatusCode);
    }
}
