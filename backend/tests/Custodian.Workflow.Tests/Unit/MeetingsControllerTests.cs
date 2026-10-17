using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services.Meetings;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

public class MeetingsControllerTests
{
    private const string Tenant = "tenant-a";
    private static readonly Guid EngagementId = Guid.NewGuid();
    private static readonly Guid MeetingId = Guid.NewGuid();

    private readonly Mock<IMeetingService> _meetings = new();
    private readonly Mock<IEngagementRepository> _engagements = new();

    public MeetingsControllerTests()
    {
        WithEngagement(EngagementStatus.Started);
        _meetings.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meeting());
    }

    private void WithEngagement(EngagementStatus? status) =>
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, Tenant))
            .ReturnsAsync(status is null ? null : new Engagement { EngagementId = EngagementId, TenantId = Tenant, Status = status.Value });

    private MeetingsController Controller(params Claim[] claims) =>
        new(_meetings.Object, _engagements.Object) { ControllerContext = ContextFor(claims) };

    private MeetingsController StaffController() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, "staff-1"));

    private static MeetingResponse Meeting() => new() { MeetingId = MeetingId, EngagementId = EngagementId, TenantId = Tenant };

    // ---- Tenant resolution (the same rules guard every action) ----

    [Fact]
    public async Task List_UsesTenantFromJwt_Trimmed()
    {
        _meetings.Setup(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync([Meeting()]);

        var result = await Controller(new Claim("tenant_id", "  tenant-a  ")).List(EngagementId, tenantId: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MeetingResponse>>(ok.Value));
    }

    [Fact]
    public async Task List_AcceptsTheCamelCaseTenantIdClaim()
    {
        await Controller(new Claim("tenantId", Tenant)).List(EngagementId, tenantId: null);

        _meetings.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_PrefersSnakeCaseTenantClaim_WhenBothArePresent()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("tenantId", "other-tenant")).List(EngagementId, tenantId: null);

        _meetings.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_QueryTenantMatchingJwtIgnoringCase_IsAllowed()
    {
        var result = await StaffController().List(EngagementId, tenantId: " TENANT-A ");

        Assert.IsType<OkObjectResult>(result.Result);
        _meetings.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_QueryTenantDifferentFromJwt_IsForbidden()
    {
        var result = await StaffController().List(EngagementId, tenantId: "tenant-b");

        Assert.IsType<ForbidResult>(result.Result);
        _meetings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_NoJwtTenant_FallsBackToTrimmedQueryTenant()
    {
        await Controller().List(EngagementId, tenantId: "  tenant-a ");

        _meetings.Verify(s => s.ListForEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task List_NoTenantAnywhere_IsBadRequest(string? queryTenant)
    {
        var result = await Controller().List(EngagementId, queryTenant);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Tenant identification is required.", Message(result.Result));
    }

    [Fact]
    public async Task EveryAction_RejectsAMismatchedTenant()
    {
        var c = StaffController();

        Assert.IsType<ForbidResult>((await c.Create(EngagementId, new CreateMeetingRequest(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.Get(EngagementId, MeetingId, "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.Update(EngagementId, MeetingId, new UpdateMeetingRequest(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), "tenant-b")).Result);
        _meetings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryAction_RequiresATenant()
    {
        var c = Controller();

        Assert.Equal("Tenant identification is required.", Message((await c.Create(EngagementId, new CreateMeetingRequest(), null)).Result));
        Assert.Equal("Tenant identification is required.", Message((await c.Get(EngagementId, MeetingId, null)).Result));
        Assert.Equal("Tenant identification is required.", Message((await c.Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null)).Result));
        Assert.Equal("Tenant identification is required.", Message((await c.UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest(), null)).Result));
        Assert.Equal("Tenant identification is required.", Message((await c.Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null)).Result));
        _meetings.VerifyNoOtherCalls();
    }

    // ---- Invalid model and terminal engagements ----

    [Fact]
    public async Task Mutations_WithInvalidModel_AreBadRequest_AndDoNotTouchTheEngagement()
    {
        var c = StaffController();
        c.ModelState.AddModelError("Purpose", "Required");

        Assert.IsType<BadRequestObjectResult>((await c.Create(EngagementId, new CreateMeetingRequest(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null)).Result);
        _engagements.VerifyNoOtherCalls();
        _meetings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mutations_OnMissingEngagement_AreNotFound()
    {
        WithEngagement(null);
        var c = StaffController();

        Assert.IsType<NotFoundResult>((await c.Create(EngagementId, new CreateMeetingRequest(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null)).Result);
        _meetings.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement is closed and cannot accept this change.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot accept this change.")]
    public async Task Mutations_OnTerminalEngagement_AreConflict(EngagementStatus status, string message)
    {
        WithEngagement(status);
        var c = StaffController();

        var results = new[]
        {
            (await c.Create(EngagementId, new CreateMeetingRequest(), null)).Result,
            (await c.Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null)).Result,
            (await c.UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest(), null)).Result,
            (await c.Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null)).Result
        };

        Assert.All(results, r =>
        {
            Assert.IsType<ConflictObjectResult>(r);
            Assert.Equal(message, Message(r));
        });
        _meetings.VerifyNoOtherCalls();
    }

    // ---- Create ----

    [Fact]
    public async Task Create_ReturnsCreatedAtGet_WithRouteValues()
    {
        var request = new CreateMeetingRequest { Purpose = "Kick-off" };
        _meetings.Setup(s => s.CreateAsync(EngagementId, Tenant, "staff-1", request, It.IsAny<CancellationToken>())).ReturnsAsync(Meeting());

        var result = await StaffController().Create(EngagementId, request, null);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(MeetingsController.Get), created.ActionName);
        Assert.Equal(EngagementId, created.RouteValues!["engagementId"]);
        Assert.Equal(MeetingId, created.RouteValues["meetingId"]);
        Assert.Equal(Tenant, created.RouteValues["tenantId"]);
        Assert.Same(request, _meetings.Invocations.Single().Arguments[3]);
    }

    [Fact]
    public async Task Create_DraftEngagement_IsStillMutable()
    {
        WithEngagement(EngagementStatus.Draft);
        _meetings.Setup(s => s.CreateAsync(EngagementId, Tenant, It.IsAny<string>(), It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meeting());

        var result = await StaffController().Create(EngagementId, new CreateMeetingRequest(), null);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Create_ArgumentException_IsBadRequestWithItsMessage()
    {
        _meetings.Setup(s => s.CreateAsync(EngagementId, Tenant, It.IsAny<string>(), It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unknown meeting type."));

        var result = await StaffController().Create(EngagementId, new CreateMeetingRequest(), null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Unknown meeting type.", Message(result.Result));
    }

    [Fact]
    public async Task Create_KeyNotFound_IsNotFound()
    {
        _meetings.Setup(s => s.CreateAsync(EngagementId, Tenant, It.IsAny<string>(), It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await StaffController().Create(EngagementId, new CreateMeetingRequest(), null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Actor resolution ----

    [Fact]
    public async Task Actor_FallsBackToSubClaim()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("sub", "user-from-sub"))
            .Create(EngagementId, new CreateMeetingRequest(), null);

        _meetings.Verify(s => s.CreateAsync(EngagementId, Tenant, "user-from-sub", It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Actor_DefaultsToSystem_WhenNoIdentityClaim()
    {
        await Controller(new Claim("tenant_id", Tenant)).Create(EngagementId, new CreateMeetingRequest(), null);

        _meetings.Verify(s => s.CreateAsync(EngagementId, Tenant, "System", It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Actor_PrefersNameIdentifierOverSub()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("sub", "from-sub"), new Claim(ClaimTypes.NameIdentifier, "from-nameid"))
            .Create(EngagementId, new CreateMeetingRequest(), null);

        _meetings.Verify(s => s.CreateAsync(EngagementId, Tenant, "from-nameid", It.IsAny<CreateMeetingRequest>(), It.IsAny<CancellationToken>()));
    }

    // ---- Get ----

    [Fact]
    public async Task Get_Found_IsOk()
    {
        var meeting = Meeting();
        _meetings.Setup(s => s.GetAsync(EngagementId, MeetingId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(meeting);

        var result = await StaffController().Get(EngagementId, MeetingId, null);

        Assert.Same(meeting, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Get_Missing_IsNotFound()
    {
        var result = await StaffController().Get(EngagementId, MeetingId, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Update, UpdateStatus, Reschedule ----

    [Fact]
    public async Task Update_Found_IsOk_PassingActorAndRequest()
    {
        var request = new UpdateMeetingRequest { Purpose = "Moved" };
        var meeting = Meeting();
        _meetings.Setup(s => s.UpdateAsync(EngagementId, MeetingId, Tenant, "staff-1", request, It.IsAny<CancellationToken>())).ReturnsAsync(meeting);

        var result = await StaffController().Update(EngagementId, MeetingId, request, null);

        Assert.Same(meeting, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Update_Missing_IsNotFound()
    {
        var result = await StaffController().Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_ArgumentException_IsBadRequestWithItsMessage()
    {
        _meetings.Setup(s => s.UpdateAsync(EngagementId, MeetingId, Tenant, It.IsAny<string>(), It.IsAny<UpdateMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Bad time."));

        var result = await StaffController().Update(EngagementId, MeetingId, new UpdateMeetingRequest(), null);

        Assert.Equal("Bad time.", Message(result.Result));
    }

    [Fact]
    public async Task UpdateStatus_PassesTheRequestedStatus()
    {
        var meeting = Meeting();
        _meetings.Setup(s => s.UpdateStatusAsync(EngagementId, MeetingId, Tenant, "staff-1", "Completed", It.IsAny<CancellationToken>())).ReturnsAsync(meeting);

        var result = await StaffController().UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest { Status = "Completed" }, null);

        Assert.Same(meeting, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpdateStatus_Missing_IsNotFound()
    {
        var result = await StaffController().UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest { Status = "Completed" }, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_ArgumentException_IsBadRequestWithItsMessage()
    {
        _meetings.Setup(s => s.UpdateStatusAsync(EngagementId, MeetingId, Tenant, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unknown status."));

        var result = await StaffController().UpdateStatus(EngagementId, MeetingId, new UpdateMeetingStatusRequest { Status = "Nope" }, null);

        Assert.Equal("Unknown status.", Message(result.Result));
    }

    [Fact]
    public async Task Reschedule_Found_IsOk_PassingActorAndRequest()
    {
        var request = new RescheduleMeetingRequest { NewScheduledAtUtc = DateTime.UtcNow.AddDays(1) };
        var meeting = Meeting();
        _meetings.Setup(s => s.RescheduleAsync(EngagementId, MeetingId, Tenant, "staff-1", request, It.IsAny<CancellationToken>())).ReturnsAsync(meeting);

        var result = await StaffController().Reschedule(EngagementId, MeetingId, request, null);

        Assert.Same(meeting, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Reschedule_Missing_IsNotFound()
    {
        var result = await StaffController().Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Reschedule_ArgumentException_IsBadRequestWithItsMessage()
    {
        _meetings.Setup(s => s.RescheduleAsync(EngagementId, MeetingId, Tenant, It.IsAny<string>(), It.IsAny<RescheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Must be in the future."));

        var result = await StaffController().Reschedule(EngagementId, MeetingId, new RescheduleMeetingRequest(), null);

        Assert.Equal("Must be in the future.", Message(result.Result));
    }
}
