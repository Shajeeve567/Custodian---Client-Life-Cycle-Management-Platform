using System.Security.Claims;
using Custodian.Audit.Controllers;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Custodian.Audit.Services.EngagementAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// Staff only read the audit trail of engagements they are the responsible staff for (Workflow decides,
/// asked as the caller); Owners read the whole workspace. Fails closed when Workflow cannot confirm.
/// </summary>
public class AuditStaffAccessTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _assigned = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Mock<IAuditEventService> _service = new();
    private readonly Mock<IEngagementAccessClient> _access = new();

    public AuditStaffAccessTests()
    {
        _service.Setup(s => s.GetEventsByTenantAsync(_tenant)).ReturnsAsync(new[] { Event(_assigned), Event(_other), Event(_assigned) });
        _access.Setup(a => a.CanAccessEngagementAsync(_assigned, _tenant, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _access.Setup(a => a.CanAccessEngagementAsync(_other, _tenant, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _access.Setup(a => a.GetAccessibleEngagementIdsAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { _assigned });
    }

    private AuditEventResponse Event(Guid engagementId) => new()
    {
        EventId = Guid.NewGuid(), EngagementId = engagementId, TenantId = _tenant, Actor = "someone", Type = "StageChange", Payload = "{}"
    };

    private AuditEventsController Controller(string role, IEngagementAccessClient? access = null, bool withAccess = true)
    {
        var controller = new AuditEventsController(_service.Object, null, withAccess ? access ?? _access.Object : null);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", _tenant.ToString()),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, $"{role.ToLowerInvariant()}-1")
        ], "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    [Fact]
    public async Task Staff_EventList_OnlyHasTheirEngagements()
    {
        var result = await Controller("Staff").GetEvents(null);

        var events = Assert.IsAssignableFrom<IEnumerable<AuditEventResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, events.Count());
        Assert.All(events, e => Assert.Equal(_assigned, e.EngagementId));
    }

    [Fact]
    public async Task Owner_EventList_HasEverything_WithoutAskingWorkflow()
    {
        var result = await Controller("Owner").GetEvents(null);

        var events = Assert.IsAssignableFrom<IEnumerable<AuditEventResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, events.Count());
        Assert.Empty(_access.Invocations);
    }

    [Fact]
    public async Task Staff_OtherEngagement_TrailAndVerify_AreNotFound()
    {
        var controller = Controller("Staff");

        Assert.IsType<NotFoundObjectResult>((await controller.GetEventsByEngagement(_other, null)).Result);
        Assert.IsType<NotFoundObjectResult>((await controller.VerifyChain(_other, null)).Result);
        _service.Verify(s => s.GetEventsByEngagementAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        _service.Verify(s => s.VerifyChainAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Staff_OwnEngagement_TrailIsReturned()
    {
        _service.Setup(s => s.GetEventsByEngagementAsync(_assigned, _tenant)).ReturnsAsync(new[] { Event(_assigned) });

        var result = await Controller("Staff").GetEventsByEngagement(_assigned, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Staff_SingleEventOfAnotherEngagement_IsNotFound()
    {
        var hidden = Event(_other);
        _service.Setup(s => s.GetEventByIdAsync(hidden.EventId, _tenant)).ReturnsAsync(hidden);

        var result = await Controller("Staff").GetEventById(hidden.EventId, null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Staff_WorkflowUnavailable_Is503_FailClosed()
    {
        var down = new Mock<IEngagementAccessClient>();
        down.Setup(a => a.GetAccessibleEngagementIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EngagementAccessUnavailableException("down"));
        down.Setup(a => a.CanAccessEngagementAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EngagementAccessUnavailableException("down"));
        var controller = Controller("Staff", down.Object);

        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.GetEvents(null)).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.GetEventsByEngagement(_assigned, null)).Result).StatusCode);
    }

    [Fact]
    public async Task Staff_WithoutAWorkflowClient_Is503_NotEverything()
    {
        var result = await Controller("Staff", withAccess: false).GetEvents(null);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }
}
