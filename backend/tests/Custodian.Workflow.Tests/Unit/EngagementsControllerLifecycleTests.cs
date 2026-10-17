using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Stall;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// EngagementsController endpoints not covered by <see cref="EngagementsControllerUnitTests"/>:
/// next action / recheck (CSTD-19, CSTD-35), readiness, handoff and closure (CSTD-20), and the
/// client ownership and staff assignment filters on the read endpoints.
/// </summary>
public class EngagementsControllerLifecycleTests
{
    private const string Tenant = "tenant-a";
    private static readonly Guid Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly Mock<IEngagementRepository> _repo = new();
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly Mock<INextActionService> _nextAction = new();
    private readonly Mock<IStallRecorder> _stalls = new();
    private readonly Mock<IReadinessService> _readiness = new();
    private readonly Mock<IClientActionService> _actions = new();
    private readonly Mock<IGateEvaluator> _gates = new();
    private readonly List<(string Type, object Payload)> _published = [];

    public EngagementsControllerLifecycleTests()
    {
        _gates.Setup(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());
        _repo.Setup(r => r.CreateAsync(It.IsAny<Engagement>())).ReturnsAsync((Engagement e) => e);
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Engagement>())).ReturnsAsync((Engagement e) => e);
        _audit.Setup(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback((Guid _, string _, string _, string type, object payload) => _published.Add((type, payload)))
            .Returns(Task.CompletedTask);
    }

    private Engagement WithEngagement(
        EngagementStatus status, string clientId = "client-1", string staffId = "staff-1", EngagementStage stage = EngagementStage.Closure)
    {
        var engagement = new Engagement
        {
            EngagementId = Id,
            TenantId = Tenant,
            ClientId = clientId,
            StaffId = staffId,
            Status = status,
            Stage = stage
        };
        _repo.Setup(r => r.GetByIdAsync(Id, Tenant)).ReturnsAsync(engagement);
        return engagement;
    }

    private void Readiness(bool isReady, params ReadinessBlocker[] blockers) =>
        _readiness.Setup(r => r.EvaluateAsync(Id, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadinessResponse { EngagementId = Id, IsReady = isReady, Blockers = blockers.ToList() });

    private EngagementsController Controller(
        bool withNextAction = true, bool withReadiness = true, bool withStalls = true, bool withActions = false, params Claim[] claims) =>
        new(_repo.Object, _audit.Object, _gates.Object, actionService: withActions ? _actions.Object : null,
            nextActionService: withNextAction ? _nextAction.Object : null,
            stallRecorder: withStalls ? _stalls.Object : null,
            readiness: withReadiness ? _readiness.Object : null)
        {
            ControllerContext = ContextFor(claims.Length > 0
                ? claims
                : [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1")])
        };

    private static object? Prop(object source, string name) => source.GetType().GetProperty(name)?.GetValue(source);

    // ==========================================
    // Next action and recheck
    // ==========================================

    [Fact]
    public async Task GetNextAction_ReturnsTheStaffViewResult()
    {
        WithEngagement(EngagementStatus.Started);
        var next = new NextActionResult { EngagementId = Id };
        _nextAction.Setup(s => s.GetNextActionAsync(Id, Tenant, NextActionView.Staff, It.IsAny<CancellationToken>())).ReturnsAsync(next);

        var result = await Controller().GetNextAction(Id, tenantId: null);

        Assert.Same(next, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task GetNextAction_MissingEngagement_IsNotFoundWithMessage()
    {
        var result = await Controller().GetNextAction(Id, tenantId: null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal($"Engagement '{Id}' was not found in tenant '{Tenant}'.", Message(result.Result));
        _nextAction.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetNextAction_ServiceNotConfigured_Is500()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller(withNextAction: false).GetNextAction(Id, tenantId: null);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Next action evaluation service is not configured.", Message(error));
    }

    [Fact]
    public async Task GetNextAction_NoResult_IsNotFoundWithMessage()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller().GetNextAction(Id, tenantId: null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal($"Next action could not be evaluated for engagement '{Id}'.", Message(result.Result));
    }

    [Fact]
    public async Task GetNextAction_TenantChecks()
    {
        Assert.IsType<ForbidResult>((await Controller().GetNextAction(Id, "tenant-b")).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).GetNextAction(Id, null);
        Assert.Equal("tenantId parameter or JWT tenant claim is required for tenant isolation.",
            Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);
    }

    [Fact]
    public async Task Recheck_ReturnsTheStaffViewResult()
    {
        WithEngagement(EngagementStatus.Started);
        var next = new NextActionResult { EngagementId = Id };
        _nextAction.Setup(s => s.GetNextActionAsync(Id, Tenant, NextActionView.Staff, It.IsAny<CancellationToken>())).ReturnsAsync(next);

        var result = await Controller().Recheck(Id, tenantId: null);

        Assert.Same(next, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Recheck_MissingEngagement_IsNotFound()
    {
        var result = await Controller().Recheck(Id, tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Recheck_ServiceNotConfigured_Is500()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller(withNextAction: false).Recheck(Id, tenantId: null);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Next action evaluation service is not configured.", Message(error));
    }

    [Fact]
    public async Task Recheck_NoResult_IsNotFoundWithMessage()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller().Recheck(Id, tenantId: null);

        Assert.Equal($"Next action could not be evaluated for engagement '{Id}'.", Message(result.Result));
    }

    [Fact]
    public async Task Recheck_TenantChecks()
    {
        Assert.IsType<ForbidResult>((await Controller().Recheck(Id, "tenant-b")).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).Recheck(Id, null);
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);
    }

    // ==========================================
    // Readiness
    // ==========================================

    [Fact]
    public async Task GetReadiness_ReturnsTheEvaluation()
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);

        var result = await Controller().GetReadiness(Id, tenantId: null);

        var readiness = Assert.IsType<ReadinessResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task GetReadiness_MissingEngagement_IsNotFound_WithoutEvaluating()
    {
        var result = await Controller().GetReadiness(Id, tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
        _readiness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetReadiness_EvaluatorSaysNotFound_IsNotFound()
    {
        WithEngagement(EngagementStatus.Started);
        _readiness.Setup(r => r.EvaluateAsync(Id, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        var result = await Controller().GetReadiness(Id, tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetReadiness_ServiceNotConfigured_Is500()
    {
        var result = await Controller(withReadiness: false).GetReadiness(Id, tenantId: null);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Readiness service is not configured.", Message(error));
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetReadiness_TenantChecks()
    {
        Assert.IsType<ForbidResult>((await Controller().GetReadiness(Id, "tenant-b")).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).GetReadiness(Id, null);
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);
    }

    // ==========================================
    // Handoff
    // ==========================================

    [Fact]
    public async Task Handoff_Ready_RecordsTrimmedSummaryActorAndTime_AndPublishesEngagementReady()
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);
        var before = DateTime.UtcNow;

        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "  Delivered pack  " }, tenantId: null);

        var response = Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Delivered pack", response.HandoffSummary);
        Assert.Equal("staff-1", response.HandedOffBy);
        Assert.InRange(response.HandedOffAtUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal("Started", response.Status);
        _repo.Verify(r => r.UpdateAsync(It.Is<Engagement>(e => e.HandoffSummary == "Delivered pack")), Times.Once);

        var (type, payload) = Assert.Single(_published);
        Assert.Equal("EngagementReady", type);
        Assert.Equal("client-1", Prop(payload, "clientId"));
        Assert.Equal("Delivered pack", Prop(payload, "handoffSummary"));
        Assert.Equal("staff-1", Prop(payload, "handedOffBy"));
        Assert.Equal(response.HandedOffAtUtc, Prop(payload, "handedOffAtUtc"));
        _audit.Verify(a => a.PublishEventAsync(Id, Tenant, "staff-1", "EngagementReady", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task Handoff_NotReady_IsConflictWithBlockers_AndChangesNothing()
    {
        WithEngagement(EngagementStatus.Started);
        var blocker = new ReadinessBlocker { Kind = "Task", Title = "Sign-off", Reason = "Pending" };
        Readiness(false, blocker);

        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "x" }, tenantId: null);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("Engagement is not ready for handoff.", Message(conflict));
        Assert.Same(blocker, Assert.Single(Assert.IsAssignableFrom<IEnumerable<ReadinessBlocker>>(Prop(conflict.Value!, "blockers"))));
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task Handoff_TerminalEngagement_IsConflict_WithoutEvaluating(EngagementStatus status)
    {
        WithEngagement(status);

        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "x" }, tenantId: null);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal($"Engagement in status '{status}' cannot be handed off.", Message(result.Result));
        _readiness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handoff_DraftEngagement_IsEvaluatedNotRejected()
    {
        WithEngagement(EngagementStatus.Draft);
        Readiness(true);

        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "x" }, tenantId: null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Handoff_MissingEngagement_IsNotFound()
    {
        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "x" }, tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
        _readiness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handoff_EvaluatorSaysNotFound_IsNotFound()
    {
        WithEngagement(EngagementStatus.Started);
        _readiness.Setup(r => r.EvaluateAsync(Id, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        var result = await Controller().Handoff(Id, new HandoffEngagementRequest { Summary = "x" }, tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handoff_GuardsRunBeforeAnyLookup()
    {
        var invalid = Controller();
        invalid.ModelState.AddModelError("Summary", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.Handoff(Id, new HandoffEngagementRequest(), null)).Result);

        Assert.IsType<ForbidResult>((await Controller().Handoff(Id, new HandoffEngagementRequest(), "tenant-b")).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).Handoff(Id, new HandoffEngagementRequest(), null);
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);

        var notConfigured = await Controller(withReadiness: false).Handoff(Id, new HandoffEngagementRequest(), null);
        Assert.Equal(500, Assert.IsType<ObjectResult>(notConfigured.Result).StatusCode);
        Assert.Equal("Readiness service is not configured.", Message(notConfigured.Result));

        _repo.VerifyNoOtherCalls();
    }

    // ==========================================
    // Close
    // ==========================================

    [Fact]
    public async Task Close_Ready_ClosesWithActorTimeAndTrimmedSummary_ResolvesStalls_AndPublishes()
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);
        var before = DateTime.UtcNow;

        var result = await Controller().Close(Id, new CloseEngagementRequest { Summary = "  All done " }, tenantId: null);

        var response = Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Closed", response.Status);
        Assert.Equal("staff-1", response.ClosedBy);
        Assert.Equal("All done", response.ClosureSummary);
        Assert.InRange(response.ClosedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(Id, response.EngagementId);
        Assert.Equal(Tenant, response.TenantId);
        Assert.Equal("client-1", response.ClientId);
        Assert.Equal("staff-1", response.StaffId);
        Assert.Equal("Closure", response.Stage);
        Assert.Equal(100, response.StageProgressPercentage);

        _stalls.Verify(s => s.ResolveForEngagementAsync(Id, Tenant, StallResolution.EngagementClosed, It.IsAny<CancellationToken>()), Times.Once);

        var (type, payload) = Assert.Single(_published);
        Assert.Equal("EngagementClosed", type);
        Assert.Equal("client-1", Prop(payload, "clientId"));
        Assert.Equal("All done", Prop(payload, "closureSummary"));
        Assert.Equal("staff-1", Prop(payload, "closedBy"));
        Assert.Equal(response.ClosedAt, Prop(payload, "closedAt"));
        _audit.Verify(a => a.PublishEventAsync(Id, Tenant, "staff-1", "EngagementClosed", It.IsAny<object>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Close_BlankSummary_IsStoredAsNull(string? summary)
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);

        var result = await Controller().Close(Id, new CloseEngagementRequest { Summary = summary }, tenantId: null);

        Assert.Null(Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).ClosureSummary);
    }

    [Fact]
    public async Task Close_WithoutStallRecorder_StillCloses()
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);

        var result = await Controller(withStalls: false).Close(Id, new CloseEngagementRequest(), tenantId: null);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Close_AlreadyClosed_IsIdempotent_NoUpdateOrEvent()
    {
        var engagement = WithEngagement(EngagementStatus.Closed);
        engagement.ClosedBy = "owner-1";

        var result = await Controller().Close(Id, new CloseEngagementRequest { Summary = "again" }, tenantId: null);

        var response = Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Closed", response.Status);
        Assert.Equal("owner-1", response.ClosedBy);
        _readiness.VerifyNoOtherCalls();
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot be closed.")]
    [InlineData(EngagementStatus.Draft, "Start the engagement before closing it.")]
    public async Task Close_CancelledOrDraft_IsConflict(EngagementStatus status, string message)
    {
        WithEngagement(status);

        var result = await Controller().Close(Id, new CloseEngagementRequest(), tenantId: null);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(message, Message(result.Result));
        _readiness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Close_NotReady_IsConflictWithBlockers_AndChangesNothing()
    {
        var engagement = WithEngagement(EngagementStatus.Started);
        var blocker = new ReadinessBlocker { Kind = "Condition", Title = "Retainer", Reason = "Unpaid" };
        Readiness(false, blocker);

        var result = await Controller().Close(Id, new CloseEngagementRequest(), tenantId: null);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("Engagement is not ready for closure.", Message(conflict));
        Assert.Same(blocker, Assert.Single(Assert.IsAssignableFrom<IEnumerable<ReadinessBlocker>>(Prop(conflict.Value!, "blockers"))));
        Assert.Equal(EngagementStatus.Started, engagement.Status);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
        _stalls.VerifyNoOtherCalls();
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Close_EvaluatorSaysNotFound_IsNotFound()
    {
        WithEngagement(EngagementStatus.Started);
        _readiness.Setup(r => r.EvaluateAsync(Id, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        var result = await Controller().Close(Id, new CloseEngagementRequest(), tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Close_MissingEngagement_IsNotFound()
    {
        var result = await Controller().Close(Id, new CloseEngagementRequest(), tenantId: null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Close_GuardsRunBeforeAnyLookup()
    {
        var invalid = Controller();
        invalid.ModelState.AddModelError("Summary", "Too long");
        Assert.IsType<BadRequestObjectResult>((await invalid.Close(Id, new CloseEngagementRequest(), null)).Result);

        Assert.IsType<ForbidResult>((await Controller().Close(Id, new CloseEngagementRequest(), "tenant-b")).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).Close(Id, new CloseEngagementRequest(), null);
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);

        var notConfigured = await Controller(withReadiness: false).Close(Id, new CloseEngagementRequest(), null);
        Assert.Equal(500, Assert.IsType<ObjectResult>(notConfigured.Result).StatusCode);
        Assert.Equal("Readiness service is not configured.", Message(notConfigured.Result));

        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Close_ActorFallsBackToSub_ThenSystem()
    {
        WithEngagement(EngagementStatus.Started);
        Readiness(true);

        var bySub = await Controller(claims: [new Claim("tenant_id", Tenant), new Claim("sub", "from-sub")])
            .Close(Id, new CloseEngagementRequest(), null);
        Assert.Equal("from-sub", Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(bySub.Result).Value).ClosedBy);

        WithEngagement(EngagementStatus.Started);
        var bySystem = await Controller(claims: new Claim("tenant_id", Tenant)).Close(Id, new CloseEngagementRequest(), null);
        Assert.Equal("System", Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(bySystem.Result).Value).ClosedBy);
    }

    // ==========================================
    // Client ownership and staff assignment on reads
    // ==========================================

    private static Claim[] ClientClaims(params Claim[] identity) =>
        [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), .. identity];

    [Theory]
    [InlineData("client_id")]
    [InlineData("clientId")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task GetEngagementById_Client_CanReadOwnEngagement(string claimType)
    {
        WithEngagement(EngagementStatus.Started, clientId: "client-1");

        var result = await Controller(claims: ClientClaims(new Claim(claimType, " CLIENT-1 "))).GetEngagementById(Id, null);

        Assert.Equal(Id, Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).EngagementId);
    }

    [Fact]
    public async Task GetEngagementById_Client_OtherClientsEngagement_IsNotFound()
    {
        WithEngagement(EngagementStatus.Started, clientId: "client-2");

        var result = await Controller(claims: ClientClaims(new Claim("client_id", "client-1"))).GetEngagementById(Id, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetEngagementById_Client_WithoutIdentity_IsNotFound()
    {
        WithEngagement(EngagementStatus.Started, clientId: "");

        var result = await Controller(claims: ClientClaims()).GetEngagementById(Id, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetEngagementById_Client_ClientIdClaimWinsOverSub()
    {
        WithEngagement(EngagementStatus.Started, clientId: "client-1");

        var result = await Controller(claims: ClientClaims(new Claim("client_id", "client-1"), new Claim("sub", "user-9")))
            .GetEngagementById(Id, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEngagements_Client_SeesOnlyOwnEngagements()
    {
        _repo.Setup(r => r.GetAllByTenantAsync(Tenant)).ReturnsAsync(
        [
            new Engagement { TenantId = Tenant, ClientId = "client-1", StaffId = "staff-9" },
            new Engagement { TenantId = Tenant, ClientId = "client-2", StaffId = "staff-9" }
        ]);

        var result = await Controller(claims: ClientClaims(new Claim("client_id", "client-1"))).GetEngagements(null);

        var list = Assert.IsAssignableFrom<IEnumerable<EngagementResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("client-1", Assert.Single(list).ClientId);
    }

    [Fact]
    public async Task GetEngagements_Staff_SeesOnlyAssignedEngagements_OwnerSeesAll()
    {
        _repo.Setup(r => r.GetAllByTenantAsync(Tenant)).ReturnsAsync(() =>
        [
            new Engagement { TenantId = Tenant, ClientId = "client-1", StaffId = "staff-1" },
            new Engagement { TenantId = Tenant, ClientId = "client-2", StaffId = "staff-2" }
        ]);

        var staff = await Controller().GetEngagements(null);
        var owner = await Controller(claims: [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), new Claim(ClaimTypes.NameIdentifier, "owner-1")])
            .GetEngagements(null);

        var staffList = Assert.IsAssignableFrom<IEnumerable<EngagementResponse>>(Assert.IsType<OkObjectResult>(staff.Result).Value);
        Assert.Equal("staff-1", Assert.Single(staffList).StaffId);
        Assert.Equal(2, Assert.IsAssignableFrom<IEnumerable<EngagementResponse>>(Assert.IsType<OkObjectResult>(owner.Result).Value).Count());
    }

    // ==========================================
    // Delete guards
    // ==========================================

    [Fact]
    public async Task DeleteEngagement_TenantChecks_AndMissingEngagement()
    {
        Assert.IsType<ForbidResult>(await Controller().DeleteEngagement(Id, "tenant-b"));

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Staff")).DeleteEngagement(Id, null);
        Assert.Equal("tenantId parameter or JWT tenant claim is required for tenant isolation.",
            Assert.IsType<BadRequestObjectResult>(noTenant).Value);

        Assert.IsType<NotFoundResult>(await Controller().DeleteEngagement(Id, null));
        _repo.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteEngagement_StartedEngagement_ConflictMessageNamesTheState()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller().DeleteEngagement(Id, null);

        Assert.Equal(
            "Engagement in state 'Started' cannot be physically deleted per Custodian lifecycle protection rules.",
            Message(Assert.IsType<ConflictObjectResult>(result)));
        _repo.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteEngagement_Draft_DeletesFromTheRepository()
    {
        WithEngagement(EngagementStatus.Draft);

        Assert.IsType<NoContentResult>(await Controller().DeleteEngagement(Id, null));

        _repo.Verify(r => r.DeleteAsync(Id, Tenant), Times.Once);
    }

    // ==========================================
    // Tenant and client claim precedence
    // ==========================================

    [Fact]
    public async Task Tenant_CamelCaseClaimAlone_IsAccepted()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller(claims: [new Claim("tenantId", Tenant), new Claim(ClaimTypes.Role, "Owner")]).GetEngagementById(Id, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Tenant_SnakeCaseClaimWins_WhenBothArePresent()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller(claims: [new Claim("tenant_id", Tenant), new Claim("tenantId", "other"), new Claim(ClaimTypes.Role, "Owner")])
            .GetEngagementById(Id, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEngagementById_NoTenant_HasExactMessage()
    {
        var result = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).GetEngagementById(Id, null);

        Assert.Equal("tenantId parameter or JWT tenant claim is required for tenant isolation.",
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task GetEngagements_NoTenant_HasExactMessage()
    {
        var result = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).GetEngagements(null);

        Assert.Equal("tenantId parameter or JWT tenant claim is required for tenant isolation.",
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task GetEngagementById_Client_CamelCaseClientIdWinsOverSubject()
    {
        WithEngagement(EngagementStatus.Started, clientId: "client-1");

        var result = await Controller(claims: ClientClaims(new Claim("clientId", "client-1"), new Claim(ClaimTypes.NameIdentifier, "user-9")))
            .GetEngagementById(Id, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEngagementById_Client_NameIdentifierWinsOverSub()
    {
        WithEngagement(EngagementStatus.Started, clientId: "client-1");

        var result = await Controller(claims: ClientClaims(new Claim(ClaimTypes.NameIdentifier, "client-1"), new Claim("sub", "user-9")))
            .GetEngagementById(Id, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ==========================================
    // Create
    // ==========================================

    private static CreateEngagementRequest CreateRequest() => new() { TenantId = Tenant, ClientId = "client-1", StaffId = "requested-staff" };

    [Fact]
    public async Task Create_Staff_IsAlwaysTheResponsibleStaff()
    {
        var result = await Controller().CreateEngagement(CreateRequest());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal("staff-1", Assert.IsType<EngagementResponse>(created.Value).StaffId);
    }

    [Fact]
    public async Task Create_StaffWithoutUserId_KeepsTheRequestedStaff()
    {
        var result = await Controller(claims: [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff")]).CreateEngagement(CreateRequest());

        Assert.Equal("requested-staff", Assert.IsType<EngagementResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).StaffId);
    }

    [Fact]
    public async Task Create_Owner_AssignsTheRequestedStaff()
    {
        var result = await Controller(claims: [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), new Claim(ClaimTypes.NameIdentifier, "owner-1")])
            .CreateEngagement(CreateRequest());

        Assert.Equal("requested-staff", Assert.IsType<EngagementResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).StaffId);
    }

    [Fact]
    public async Task Create_Guards()
    {
        var invalid = Controller();
        invalid.ModelState.AddModelError("ClientId", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.CreateEngagement(CreateRequest())).Result);

        var mismatched = CreateRequest();
        mismatched.TenantId = "tenant-b";
        Assert.IsType<ForbidResult>((await Controller().CreateEngagement(mismatched)).Result);

        var noTenant = CreateRequest();
        noTenant.TenantId = " ";
        var missing = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).CreateEngagement(noTenant);
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(missing.Result).Value);

        _repo.Verify(r => r.CreateAsync(It.IsAny<Engagement>()), Times.Never);
    }

    // ==========================================
    // Update status
    // ==========================================

    private static UpdateEngagementStatusRequest StatusRequest(string status) => new() { TenantId = Tenant, Status = status };

    [Fact]
    public async Task UpdateStatus_Guards()
    {
        var invalid = Controller();
        invalid.ModelState.AddModelError("Status", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.UpdateStatus(Id, StatusRequest("Started"))).Result);

        Assert.IsType<ForbidResult>((await Controller().UpdateStatus(Id, new UpdateEngagementStatusRequest { TenantId = "tenant-b", Status = "Started" })).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).UpdateStatus(Id, new UpdateEngagementStatusRequest { Status = "Started" });
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);

        Assert.IsType<NotFoundResult>((await Controller().UpdateStatus(Id, StatusRequest("Started"))).Result);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Engagement>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_UnknownStatus_HasExactMessage()
    {
        WithEngagement(EngagementStatus.Draft);

        var result = await Controller().UpdateStatus(Id, StatusRequest("Paused"));

        Assert.Equal("Invalid status: 'Paused'. Valid statuses are: Draft, Started, Closed, Cancelled.",
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpdateStatus_StatusNameIsCaseInsensitive()
    {
        WithEngagement(EngagementStatus.Draft);

        var result = await Controller().UpdateStatus(Id, StatusRequest("started"));

        Assert.Equal("Started", Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Status);
    }

    [Fact]
    public async Task UpdateStatus_ToClosed_IsConflict_PointingAtTheCloseEndpoint()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller().UpdateStatus(Id, StatusRequest("Closed"));

        Assert.Equal("Closure requires the readiness gate. Use POST /api/Engagements/{id}/close instead.",
            Message(Assert.IsType<ConflictObjectResult>(result.Result)));
    }

    [Fact]
    public async Task UpdateStatus_IllegalTransition_HasExactMessage()
    {
        WithEngagement(EngagementStatus.Cancelled);

        var result = await Controller().UpdateStatus(Id, StatusRequest("Started"));

        Assert.Equal("Invalid status transition from 'Cancelled' to 'Started'.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task UpdateStatus_Cancel_SetsClosedAt_AndResolvesStalls()
    {
        WithEngagement(EngagementStatus.Started);
        var before = DateTime.UtcNow;

        var result = await Controller().UpdateStatus(Id, StatusRequest("Cancelled"));

        var response = Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Cancelled", response.Status);
        Assert.InRange(response.ClosedAt!.Value, before, DateTime.UtcNow);
        _stalls.Verify(s => s.ResolveForEngagementAsync(Id, Tenant, StallResolution.EngagementClosed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_Start_DoesNotSetClosedAtOrResolveStalls_AndActivatesStagesUpToCurrent()
    {
        WithEngagement(EngagementStatus.Draft, stage: EngagementStage.DocumentCollection);

        var result = await Controller(withActions: true).UpdateStatus(Id, StatusRequest("Started"));

        Assert.Null(Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).ClosedAt);
        _stalls.VerifyNoOtherCalls();
        _actions.Verify(a => a.ActivateStageActionsAsync(Id, Tenant, 1), Times.Once);
        _actions.Verify(a => a.ActivateStageActionsAsync(Id, Tenant, 2), Times.Once);
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateStatus_DraftToCancelled_DoesNotActivateStages()
    {
        WithEngagement(EngagementStatus.Draft);

        await Controller(withActions: true).UpdateStatus(Id, StatusRequest("Cancelled"));

        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateStatus_Cancel_WithoutStallRecorder_StillSucceeds()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller(withStalls: false).UpdateStatus(Id, StatusRequest("Cancelled"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_SameStatus_PublishesNothing()
    {
        WithEngagement(EngagementStatus.Started);

        var result = await Controller().UpdateStatus(Id, StatusRequest("Started"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(_published);
    }

    // ==========================================
    // Change responsible staff
    // ==========================================

    [Fact]
    public async Task ChangeResponsibleStaff_Guards()
    {
        var owner = () => Controller(claims: [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner")]);

        var invalid = owner();
        invalid.ModelState.AddModelError("StaffId", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.ChangeResponsibleStaff(Id, new ChangeResponsibleStaffRequest { StaffId = Guid.NewGuid().ToString() })).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).ChangeResponsibleStaff(Id, new ChangeResponsibleStaffRequest { StaffId = Guid.NewGuid().ToString() });
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);

        var badId = await owner().ChangeResponsibleStaff(Id, new ChangeResponsibleStaffRequest { StaffId = "not-a-guid" });
        Assert.Equal("staffId must be the user id of a workspace member.", Message(Assert.IsType<BadRequestObjectResult>(badId.Result)));

        _repo.VerifyNoOtherCalls();
    }

    // ==========================================
    // Update stage
    // ==========================================

    private static UpdateEngagementStageRequest StageRequest(string stage) => new() { TenantId = Tenant, Stage = stage };

    [Fact]
    public async Task UpdateStage_Guards()
    {
        var invalid = Controller();
        invalid.ModelState.AddModelError("Stage", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.UpdateStage(Id, StageRequest("Verification"))).Result);

        Assert.IsType<ForbidResult>((await Controller().UpdateStage(Id, new UpdateEngagementStageRequest { TenantId = "tenant-b", Stage = "Verification" })).Result);

        var noTenant = await Controller(claims: new Claim(ClaimTypes.Role, "Owner")).UpdateStage(Id, new UpdateEngagementStageRequest { Stage = "Verification" });
        Assert.Equal("Tenant identification is required.", Assert.IsType<BadRequestObjectResult>(noTenant.Result).Value);

        Assert.IsType<NotFoundResult>((await Controller().UpdateStage(Id, StageRequest("Verification"))).Result);
        _gates.Verify(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement in status 'Closed' cannot advance stage.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement in status 'Cancelled' cannot advance stage.")]
    [InlineData(EngagementStatus.Draft, "Start the engagement before advancing its stage.")]
    public async Task UpdateStage_NotStarted_IsConflictWithExactMessage(EngagementStatus status, string message)
    {
        WithEngagement(status, stage: EngagementStage.DocumentCollection);

        var result = await Controller().UpdateStage(Id, StageRequest("Verification"));

        Assert.Equal(message, Message(Assert.IsType<ConflictObjectResult>(result.Result)));
    }

    [Fact]
    public async Task UpdateStage_UnknownStage_ListsTheValidStages()
    {
        WithEngagement(EngagementStatus.Started, stage: EngagementStage.DocumentCollection);

        var result = await Controller().UpdateStage(Id, StageRequest("Review"));

        Assert.Equal("Invalid stage: 'Review'. Valid stages are: Onboarding, DocumentCollection, Verification, Execution, Closure.",
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpdateStage_SkippingAhead_HasExactMessage()
    {
        WithEngagement(EngagementStatus.Started, stage: EngagementStage.Onboarding);

        var result = await Controller().UpdateStage(Id, StageRequest("Execution"));

        Assert.Equal("Invalid stage transition from 'Onboarding' to 'Execution'.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task UpdateStage_Advance_ActivatesTheNewStagesActions_AndAuditsAsSystem()
    {
        WithEngagement(EngagementStatus.Started, stage: EngagementStage.DocumentCollection);

        var result = await Controller(withActions: true).UpdateStage(Id, StageRequest("verification"));

        Assert.Equal("Verification", Assert.IsType<EngagementResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Stage);
        _actions.Verify(a => a.ActivateStageActionsAsync(Id, Tenant, 3), Times.Once);
        _actions.VerifyNoOtherCalls();
        _audit.Verify(a => a.PublishEventAsync(Id, Tenant, "System", "StageChange", It.IsAny<object>()), Times.Once);
        var (_, payload) = Assert.Single(_published);
        Assert.Equal("DocumentCollection", Prop(payload, "fromStage"));
        Assert.Equal("Verification", Prop(payload, "toStage"));
    }
}
