using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
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
    private readonly EngagementsController _controller;

    public EngagementsControllerUnitTests()
    {
        _mockRepo = new Mock<IEngagementRepository>();
        _controller = new EngagementsController(_mockRepo.Object);
    }

    /// <summary>
    /// Helper method to simulate an authenticated HTTP request with specific JWT claims (e.g. tenant_id)
    /// </summary>
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
    // 6. TENANT ISOLATION & JWT CLAIM PRECEDENCE TESTS (Subtask 4)
    // ==========================================

    [Fact]
    public async Task ResolveTenantId_JwtClaimPresent_ShouldOverrideQueryParameter()
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

        // Repository should be called with "tenant-AUTHENTICATED", NOT "tenant-ATTACKER"
        _mockRepo.Setup(r => r.GetAllByTenantAsync("tenant-AUTHENTICATED"))
                 .ReturnsAsync(new List<Engagement> { engagement });

        // Act: Client passes attacker tenant in query string: ?tenantId=tenant-ATTACKER
        var actionResult = await _controller.GetEngagements("tenant-ATTACKER");

        // Assert: Controller ignores "tenant-ATTACKER" and uses JWT claim "tenant-AUTHENTICATED"
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        _mockRepo.Verify(r => r.GetAllByTenantAsync("tenant-AUTHENTICATED"), Times.Once);
        _mockRepo.Verify(r => r.GetAllByTenantAsync("tenant-ATTACKER"), Times.Never);
    }
}
