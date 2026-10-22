using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// RequirementsController branches not covered by <see cref="RequirementsControllerTests"/>: view selection
/// via X-Client-View, the guards on each endpoint, actors and the not-found messages.
/// </summary>
public class RequirementsControllerEndpointTests
{
    private const string Tenant = "tenant-a";
    private const string TenantRequired = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter.";
    private static readonly Guid EngagementId = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid RequirementId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly string MissingRequirement =
        $"Requirement '{RequirementId}' was not found for engagement '{EngagementId}' and tenant '{Tenant}'.";

    private readonly Mock<IRequirementService> _requirements = new();
    private readonly Mock<IEngagementRepository> _engagements = new();

    public RequirementsControllerEndpointTests()
    {
        WithEngagement(EngagementStatus.Started);
    }

    private void WithEngagement(EngagementStatus? status) =>
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, Tenant))
            .ReturnsAsync(status is null ? null : new Engagement { EngagementId = EngagementId, TenantId = Tenant, Status = status.Value });

    private RequirementsController Controller(params Claim[] claims) =>
        new(_requirements.Object, _engagements.Object, NullLogger<RequirementsController>.Instance) { ControllerContext = ContextFor(claims) };

    private RequirementsController Staff() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1"));

    private RequirementsController Owner() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), new Claim(ClaimTypes.NameIdentifier, "owner-1"));

    // ---- View selection ----

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("not-a-bool", true)]
    public async Task GetRequirements_XClientViewHeader_SelectsTheView(string header, bool expectedClientView)
    {
        var c = Owner();
        c.HttpContext.Request.Headers["X-Client-View"] = header;

        await c.GetRequirements(EngagementId, null, isClientView: null);

        _requirements.Verify(s => s.GetRequirementsByEngagementAsync(EngagementId, Tenant, expectedClientView, null), Times.Once);
    }

    [Fact]
    public async Task GetRequirements_OwnerRequestsStaffView_GetsIt()
    {
        await Owner().GetRequirements(EngagementId, null, isClientView: false);

        _requirements.Verify(s => s.GetRequirementsByEngagementAsync(EngagementId, Tenant, false, null), Times.Once);
    }

    [Fact]
    public async Task GetRequirements_ExplicitClientViewFalse_IgnoresTheHeader()
    {
        var c = Staff();
        c.HttpContext.Request.Headers["X-Client-View"] = "true";

        await c.GetRequirements(EngagementId, null, isClientView: false);

        _requirements.Verify(s => s.GetRequirementsByEngagementAsync(EngagementId, Tenant, false, null), Times.Once);
    }

    [Fact]
    public async Task GetRequirements_RolelessCallerUsingTheHeader_IsForbidden()
    {
        var c = Controller(new Claim("tenant_id", Tenant));
        c.HttpContext.Request.Headers["X-Client-View"] = "false";

        var result = await c.GetRequirements(EngagementId, null, isClientView: null);

        Assert.IsType<ForbidResult>(result.Result);
        _requirements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetRequirements_ReturnsTheServiceResult()
    {
        IEnumerable<RequirementResponseDto> list = [new RequirementResponseDto()];
        _requirements.Setup(s => s.GetRequirementsByEngagementAsync(EngagementId, Tenant, true, null)).ReturnsAsync(list);

        var result = await Staff().GetRequirements(EngagementId, null, null);

        Assert.Same(list, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // ---- Guards on the mutating endpoints ----

    [Fact]
    public async Task Mutations_CrossTenant_AreForbidden()
    {
        var c = Staff();

        Assert.IsType<ForbidResult>((await c.RequestRequirement(EngagementId, new RequestRequirementDto(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), "tenant-b")).Result);
        _requirements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mutations_NoTenant_AreBadRequest()
    {
        var c = Controller(new Claim(ClaimTypes.Role, "Staff"));

        Assert.Equal(TenantRequired, Message((await c.RequestRequirement(EngagementId, new RequestRequirementDto(), null)).Result));
        Assert.Equal(TenantRequired, Message((await c.SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null)).Result));
        Assert.Equal(TenantRequired, Message((await c.ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null)).Result));
        _requirements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mutations_InvalidModel_AreBadRequest_BeforeLoadingTheEngagement()
    {
        var c = Staff();
        c.ModelState.AddModelError("Value", "Required");

        Assert.IsType<BadRequestObjectResult>((await c.RequestRequirement(EngagementId, new RequestRequirementDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null)).Result);
        _engagements.VerifyNoOtherCalls();
        _requirements.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement is closed and cannot accept this change.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot accept this change.")]
    public async Task Mutations_OnTerminalEngagement_AreConflictWithMessage(EngagementStatus status, string message)
    {
        WithEngagement(status);
        var c = Staff();

        Assert.Equal(message, Message((await c.RequestRequirement(EngagementId, new RequestRequirementDto(), null)).Result));
        Assert.Equal(message, Message((await c.SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null)).Result));
        Assert.Equal(message, Message((await c.ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null)).Result));
        _requirements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mutations_OnMissingEngagement_AreNotFound()
    {
        WithEngagement(null);
        var c = Staff();

        Assert.IsType<NotFoundResult>((await c.RequestRequirement(EngagementId, new RequestRequirementDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null)).Result);
        _requirements.VerifyNoOtherCalls();
    }

    // ---- Request ----

    [Fact]
    public async Task Request_ReturnsCreatedAtGetRequirements()
    {
        var response = new RequirementResponseDto();
        _requirements.Setup(s => s.RequestRequirementAsync(EngagementId, Tenant, It.IsAny<RequestRequirementDto>())).ReturnsAsync(response);

        var result = await Staff().RequestRequirement(EngagementId, new RequestRequirementDto(), null);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(RequirementsController.GetRequirements), created.ActionName);
        Assert.Equal(EngagementId, created.RouteValues!["engagementId"]);
        Assert.Equal(Tenant, created.RouteValues["tenantId"]);
        Assert.Same(response, created.Value);
    }

    [Fact]
    public async Task Request_ArgumentException_IsBadRequestWithMessage()
    {
        _requirements.Setup(s => s.RequestRequirementAsync(EngagementId, Tenant, It.IsAny<RequestRequirementDto>()))
            .ThrowsAsync(new ArgumentException("Unknown requirement type."));

        var result = await Staff().RequestRequirement(EngagementId, new RequestRequirementDto(), null);

        Assert.Equal("Unknown requirement type.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    // ---- Submit ----

    [Fact]
    public async Task Submit_Missing_IsNotFoundWithMessage()
    {
        var result = await Staff().SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null);

        Assert.Equal(MissingRequirement, Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Submit_NotAvailableYet_IsConflictWithMessage()
    {
        _requirements.Setup(s => s.SubmitRequirementAsync(EngagementId, RequirementId, Tenant, It.IsAny<SubmitRequirementDto>(), It.IsAny<string?>()))
            .ThrowsAsync(new ClientActionNotAvailableException("Stage 2 has not started yet."));

        var result = await Staff().SubmitRequirement(EngagementId, RequirementId, new SubmitRequirementDto(), null);

        Assert.Equal("Stage 2 has not started yet.", Message(Assert.IsType<ConflictObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Submit_Client_IsScopedToItsOwnId_WithTheJwtActor()
    {
        var dto = new SubmitRequirementDto { Value = "x", SubmittedByActor = "from-body" };
        _requirements.Setup(s => s.SubmitRequirementAsync(EngagementId, RequirementId, Tenant, dto, "client-1")).ReturnsAsync(new RequirementResponseDto());

        var result = await Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), new Claim("client_id", "client-1"), new Claim("sub", "user-1"))
            .SubmitRequirement(EngagementId, RequirementId, dto, null);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("user-1", dto.SubmittedByActor);
    }

    [Fact]
    public async Task Submit_WithoutJwtActor_KeepsTheBodyActor()
    {
        var dto = new SubmitRequirementDto { SubmittedByActor = "from-body" };

        await Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner")).SubmitRequirement(EngagementId, RequirementId, dto, null);

        Assert.Equal("from-body", dto.SubmittedByActor);
        _requirements.Verify(s => s.SubmitRequirementAsync(EngagementId, RequirementId, Tenant, dto, null), Times.Once);
    }

    // ---- Review ----

    [Fact]
    public async Task Review_ReturnsTheResult_WithTheJwtActor()
    {
        var dto = new ReviewRequirementDto { ReviewerActor = "from-body" };
        var reviewed = new RequirementResponseDto();
        _requirements.Setup(s => s.ReviewRequirementAsync(EngagementId, RequirementId, Tenant, dto)).ReturnsAsync(reviewed);

        var result = await Staff().ReviewRequirement(EngagementId, RequirementId, dto, null);

        Assert.Same(reviewed, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("staff-1", dto.ReviewerActor);
    }

    [Fact]
    public async Task Review_WithoutJwtActor_KeepsTheBodyActor()
    {
        var dto = new ReviewRequirementDto { ReviewerActor = "from-body" };

        await Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner")).ReviewRequirement(EngagementId, RequirementId, dto, null);

        Assert.Equal("from-body", dto.ReviewerActor);
    }

    [Fact]
    public async Task Review_Missing_IsNotFoundWithMessage()
    {
        var result = await Staff().ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null);

        Assert.Equal(MissingRequirement, Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Review_ArgumentException_IsBadRequestWithMessage()
    {
        _requirements.Setup(s => s.ReviewRequirementAsync(EngagementId, RequirementId, Tenant, It.IsAny<ReviewRequirementDto>()))
            .ThrowsAsync(new ArgumentException("Rejection needs a reason."));

        var result = await Staff().ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null);

        Assert.Equal("Rejection needs a reason.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task RequestAndReview_ClientCaller_IsForbidden()
    {
        var client = () => Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), new Claim("client_id", "client-1"));

        Assert.IsType<ForbidResult>((await client().RequestRequirement(EngagementId, new RequestRequirementDto(), null)).Result);
        Assert.IsType<ForbidResult>((await client().ReviewRequirement(EngagementId, RequirementId, new ReviewRequirementDto(), null)).Result);
        _engagements.VerifyNoOtherCalls();
    }
}
