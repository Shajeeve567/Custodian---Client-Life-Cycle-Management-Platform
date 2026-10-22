using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

public class InterventionsControllerTests
{
    private const string Tenant = "tenant-a";
    private static readonly Guid EngagementId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly Mock<IInterventionService> _interventions = new();
    private readonly Mock<IEngagementRepository> _engagements = new();

    public InterventionsControllerTests()
    {
        WithEngagement(EngagementStatus.Started);
    }

    private void WithEngagement(EngagementStatus? status) =>
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, Tenant))
            .ReturnsAsync(status is null ? null : new Engagement { EngagementId = EngagementId, TenantId = Tenant, Status = status.Value });

    private InterventionsController Controller(params Claim[] claims) =>
        new(_interventions.Object, _engagements.Object) { ControllerContext = ContextFor(claims) };

    private InterventionsController StaffController() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, "staff-1"));

    private static RecordInterventionRequest Request() => new() { Reason = "Client unresponsive", Outcome = "Called client" };

    // ---- Record ----

    [Fact]
    public async Task Record_ReturnsCreatedAtList_WithRouteValues()
    {
        var request = Request();
        var response = new InterventionResponse { InterventionId = Guid.NewGuid() };
        _interventions.Setup(s => s.RecordAsync(EngagementId, Tenant, "staff-1", request, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await StaffController().Record(EngagementId, request, tenantId: null);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(InterventionsController.List), created.ActionName);
        Assert.Equal(EngagementId, created.RouteValues!["engagementId"]);
        Assert.Equal(Tenant, created.RouteValues["tenantId"]);
        Assert.Same(response, created.Value);
    }

    [Fact]
    public async Task Record_DraftEngagement_IsStillMutable()
    {
        WithEngagement(EngagementStatus.Draft);
        _interventions.Setup(s => s.RecordAsync(EngagementId, Tenant, It.IsAny<string>(), It.IsAny<RecordInterventionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InterventionResponse());

        var result = await StaffController().Record(EngagementId, Request(), null);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Record_MissingEngagement_IsNotFoundWithMessage()
    {
        WithEngagement(null);

        var result = await StaffController().Record(EngagementId, Request(), null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Engagement '11111111-2222-3333-4444-555555555555' not found in tenant.", Message(result.Result));
        _interventions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement is closed and cannot accept this change.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot accept this change.")]
    public async Task Record_TerminalEngagement_IsConflict(EngagementStatus status, string message)
    {
        WithEngagement(status);

        var result = await StaffController().Record(EngagementId, Request(), null);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(message, Message(result.Result));
        _interventions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Record_InvalidModel_IsBadRequest_BeforeLoadingTheEngagement()
    {
        var c = StaffController();
        c.ModelState.AddModelError("Reason", "Required");

        var result = await c.Record(EngagementId, new RecordInterventionRequest(), null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _engagements.VerifyNoOtherCalls();
        _interventions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Record_ArgumentException_IsBadRequestWithItsMessage()
    {
        _interventions.Setup(s => s.RecordAsync(EngagementId, Tenant, It.IsAny<string>(), It.IsAny<RecordInterventionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unknown intervention type."));

        var result = await StaffController().Record(EngagementId, Request(), null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Unknown intervention type.", Message(result.Result));
    }

    [Fact]
    public async Task Record_QueryTenantDifferentFromJwt_IsForbidden()
    {
        var result = await StaffController().Record(EngagementId, Request(), "tenant-b");

        Assert.IsType<ForbidResult>(result.Result);
        _engagements.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Record_NoTenantAnywhere_IsBadRequest(string? queryTenant)
    {
        var result = await Controller().Record(EngagementId, Request(), queryTenant);

        Assert.Equal("Tenant identification is required.", Message(result.Result));
        _engagements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Record_Actor_FallsBackToSub_ThenSystem()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("sub", "from-sub")).Record(EngagementId, Request(), null);
        await Controller(new Claim("tenant_id", Tenant)).Record(EngagementId, Request(), null);

        _interventions.Verify(s => s.RecordAsync(EngagementId, Tenant, "from-sub", It.IsAny<RecordInterventionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _interventions.Verify(s => s.RecordAsync(EngagementId, Tenant, "System", It.IsAny<RecordInterventionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Record_Actor_PrefersNameIdentifierOverSub()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("sub", "from-sub"), new Claim(ClaimTypes.NameIdentifier, "from-nameid"))
            .Record(EngagementId, Request(), null);

        _interventions.Verify(s => s.RecordAsync(EngagementId, Tenant, "from-nameid", It.IsAny<RecordInterventionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- List ----

    [Fact]
    public async Task List_ReturnsTheEngagementsInterventions()
    {
        IReadOnlyList<InterventionResponse> items = [new InterventionResponse(), new InterventionResponse()];
        _interventions.Setup(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(items);

        var result = await Controller(new Claim("tenantId", "  tenant-a ")).List(EngagementId, tenantId: null);

        Assert.Same(items, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task List_PrefersSnakeCaseTenantClaim_WhenBothArePresent()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("tenantId", "other-tenant")).List(EngagementId, tenantId: null);

        _interventions.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_QueryTenantMatchingJwtIgnoringCase_IsAllowed()
    {
        var result = await StaffController().List(EngagementId, " TENANT-A ");

        Assert.IsType<OkObjectResult>(result.Result);
        _interventions.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_QueryTenantDifferentFromJwt_IsForbidden()
    {
        var result = await StaffController().List(EngagementId, "tenant-b");

        Assert.IsType<ForbidResult>(result.Result);
        _interventions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_NoJwtTenant_FallsBackToTrimmedQueryTenant()
    {
        await Controller().List(EngagementId, "  tenant-a ");

        _interventions.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task List_NoTenantAnywhere_IsBadRequest(string? queryTenant)
    {
        var result = await Controller().List(EngagementId, queryTenant);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Tenant identification is required.", Message(result.Result));
    }
}
