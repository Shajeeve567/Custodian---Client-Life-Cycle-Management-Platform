using System.Security.Claims;
using Custodian.Audit.Controllers;
using Custodian.Audit.DTOs;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.EngagementAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// AuditEventsController: tenant resolution (JWT, X-Tenant-Id, query), the actor recorded on flag and
/// archive, and how flag, archive and get-by-id map missing events, denied engagements and errors.
/// </summary>
public class AuditEventsControllerResolutionTests
{
    private static readonly Guid Tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EngagementId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid EventId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");
    private const string TenantRequired = "Tenant ID could not be resolved from JWT claim or query parameters.";

    private readonly Mock<IAuditEventService> _service = new();
    private readonly Mock<IEngagementAccessClient> _access = new();
    private Guid? _tenantSeen;

    public AuditEventsControllerResolutionTests()
    {
        _service.Setup(s => s.GetEventsByTenantAsync(It.IsAny<Guid>()))
            .Callback((Guid t) => _tenantSeen = t)
            .ReturnsAsync(Array.Empty<AuditEventResponse>());
        _service.Setup(s => s.GetEventByIdAsync(EventId, Tenant))
            .ReturnsAsync(new AuditEventResponse { EventId = EventId, EngagementId = EngagementId, TenantId = Tenant });
    }

    private AuditEventsController Controller(Claim[] claims, string? tenantHeader = null, bool withAccess = true)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (tenantHeader is not null) http.Request.Headers["X-Tenant-Id"] = tenantHeader;
        return new AuditEventsController(_service.Object, null, withAccess ? _access.Object : null)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static Claim[] Owner(params Claim[] extra) =>
        [new Claim("tenant_id", Tenant.ToString()), new Claim(ClaimTypes.Role, "Owner"), .. extra];

    private static Claim[] Staff(params Claim[] extra) =>
        [new Claim("tenant_id", Tenant.ToString()), new Claim(ClaimTypes.Role, "Staff"), .. extra];

    private static string? Message(IActionResult? result)
    {
        var body = Assert.IsAssignableFrom<ObjectResult>(result).Value!;
        return body.GetType().GetProperty("message")?.GetValue(body) as string;
    }

    // ==========================================
    // Tenant resolution
    // ==========================================

    public static TheoryData<string, Claim[], string?, string?, string> TenantCases => new()
    {
        // Expected: "tenant", "other", "forbid" or "bad-request".
        { "jwt", [new Claim("tenant_id", $" {Tenant} ")], null, null, "tenant" },
        { "camelCase claim", [new Claim("tenantId", Tenant.ToString())], null, null, "tenant" },
        { "snake_case wins", [new Claim("tenant_id", Tenant.ToString()), new Claim("tenantId", OtherTenant.ToString())], null, null, "tenant" },
        { "query mismatch", [new Claim("tenant_id", Tenant.ToString())], null, OtherTenant.ToString(), "forbid" },
        { "query matches", [new Claim("tenant_id", Tenant.ToString())], null, $" {Tenant.ToString().ToUpperInvariant()} ", "tenant" },
        { "header mismatch", [new Claim("tenant_id", Tenant.ToString())], OtherTenant.ToString(), null, "forbid" },
        { "header matches", [new Claim("tenant_id", Tenant.ToString())], $" {Tenant} ", null, "tenant" },
        { "blank header ignored", [new Claim("tenant_id", Tenant.ToString())], "  ", null, "tenant" },
        { "blank claim falls through to query", [new Claim("tenant_id", " ")], null, Tenant.ToString(), "tenant" },
        { "no jwt: query beats header", [], OtherTenant.ToString(), $" {Tenant} ", "tenant" },
        { "no jwt: header", [], $" {OtherTenant} ", null, "other" },
        { "no jwt: blank header", [], " ", null, "bad-request" },
        { "nothing", [], null, null, "bad-request" }
    };

    [Theory]
    [MemberData(nameof(TenantCases))]
    public async Task ResolvesTheTenant(string _, Claim[] claims, string? header, string? query, string expected)
    {
        var result = await Controller([new Claim(ClaimTypes.Role, "Owner"), .. claims], header).GetEvents(query);

        switch (expected)
        {
            case "forbid":
                Assert.IsType<ForbidResult>(result.Result);
                Assert.Null(_tenantSeen);
                break;
            case "bad-request":
                Assert.Equal(TenantRequired, Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
                Assert.Null(_tenantSeen);
                break;
            default:
                Assert.IsType<OkObjectResult>(result.Result);
                Assert.Equal(expected == "tenant" ? Tenant : OtherTenant, _tenantSeen);
                break;
        }
    }

    [Fact]
    public async Task NonGuidTenantIds_AreMappedConsistently()
    {
        // A non-GUID tenant id is hashed to a stable GUID, so the same text always means the same tenant.
        await Controller(Owner()).GetEvents(null);
        var direct = _tenantSeen;

        _tenantSeen = null;
        var claims = new[] { new Claim("tenant_id", "acme"), new Claim(ClaimTypes.Role, "Owner") };
        Assert.IsType<OkObjectResult>((await Controller(claims).GetEvents(" acme ")).Result);
        var acme = _tenantSeen;
        Assert.IsType<ForbidResult>((await Controller(claims).GetEvents("globex")).Result);

        _tenantSeen = null;
        await Controller([new Claim(ClaimTypes.Role, "Owner")]).GetEvents("acme");
        Assert.Equal(acme, _tenantSeen);
        Assert.NotEqual(Guid.Empty, acme!.Value);
        Assert.NotEqual(direct, acme);
    }

    // ==========================================
    // Flag
    // ==========================================

    [Fact]
    public async Task Flag_Guards()
    {
        Assert.Equal(TenantRequired, Message((await Controller([new Claim(ClaimTypes.Role, "Owner")]).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null)).Result));
        Assert.IsType<ForbidResult>((await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, OtherTenant.ToString())).Result);
        Assert.Equal("Reason is required.", Message((await Controller(Owner()).FlagEvent(EventId, null!, null)).Result));
        Assert.Equal("Reason is required.", Message((await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "  " }, null)).Result));
        Assert.Equal("Reason must not exceed 500 characters.", Message((await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = new string('r', 501) }, null)).Result));
        _service.Verify(s => s.FlagEventAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Flag_ReasonOfExactly500CharsAfterTrim_IsAccepted()
    {
        _service.Setup(s => s.FlagEventAsync(EventId, Tenant, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new AuditEventResponse());

        var result = await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "  " + new string('r', 500) + " " }, null);

        Assert.IsType<OkObjectResult>(result.Result);
        _service.Verify(s => s.FlagEventAsync(EventId, Tenant, new string('r', 500), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Flag_UnknownEvent_IsNotFoundWithMessage()
    {
        var missing = Guid.NewGuid();

        var result = await Controller(Owner()).FlagEvent(missing, new FlagAuditEventRequest { Reason = "x" }, null);

        Assert.Equal($"Audit event with ID '{missing}' was not found for tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Flag_ServiceReturnsNothing_IsNotFoundWithMessage()
    {
        var result = await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null);

        Assert.Equal($"Audit event with ID '{EventId}' was not found for tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Flag_ServiceErrors_MapToBadRequestAndConflict()
    {
        _service.SetupSequence(s => s.FlagEventAsync(EventId, Tenant, It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new ArgumentException("Bad reason."))
            .ThrowsAsync(new AuditChainConflictException("Another tenant."));

        Assert.Equal("Bad reason.", Message(Assert.IsType<BadRequestObjectResult>((await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null)).Result)));
        Assert.Equal("Another tenant.", Message(Assert.IsType<ConflictObjectResult>((await Controller(Owner()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null)).Result)));
    }

    [Fact]
    public async Task Flag_StaffWhenWorkflowIsUnreachable_Is503()
    {
        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new EngagementAccessUnavailableException("down"));

        var result = await Controller(Staff()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("Unable to confirm access to this engagement right now. Please try again shortly.", Message(unavailable));
    }

    [Fact]
    public async Task Flag_StaffWithoutAnAccessClient_Is503()
    {
        var result = await Controller(Staff(), withAccess: false).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Flag_StaffNotAssigned_IsTheEventNotFoundMessage()
    {
        var result = await Controller(Staff()).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null);

        Assert.Equal($"Audit event with ID '{EventId}' was not found for tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    public static TheoryData<Claim[], string> Actors => new()
    {
        { [new Claim(ClaimTypes.Email, "e@x.com"), new Claim(ClaimTypes.Name, "n"), new Claim(ClaimTypes.NameIdentifier, "id"), new Claim("sub", "s"), new Claim("email", "e2")], "e@x.com" },
        { [new Claim(ClaimTypes.Name, "Jane"), new Claim(ClaimTypes.NameIdentifier, "id"), new Claim("sub", "s"), new Claim("email", "e2")], "Jane" },
        { [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("sub", "s"), new Claim("email", "e2")], "user-1" },
        { [new Claim("sub", "from-sub"), new Claim("email", "e2")], "from-sub" },
        { [new Claim("email", "raw@x.com")], "raw@x.com" },
        { [], "system" }
    };

    [Theory]
    [MemberData(nameof(Actors))]
    public async Task Flag_And_Archive_RecordTheActorFromTheToken(Claim[] identity, string expected)
    {
        await Controller(Owner(identity)).FlagEvent(EventId, new FlagAuditEventRequest { Reason = "x" }, null);
        await Controller(Owner(identity)).ArchiveEvent(EventId, null, null);

        _service.Verify(s => s.FlagEventAsync(EventId, Tenant, "x", expected), Times.Once);
        _service.Verify(s => s.ArchiveEventAsync(EventId, Tenant, null, expected), Times.Once);
    }

    // ==========================================
    // Archive
    // ==========================================

    [Fact]
    public async Task Archive_Guards()
    {
        Assert.Equal(TenantRequired, Message((await Controller([new Claim(ClaimTypes.Role, "Owner")]).ArchiveEvent(EventId, null, null)).Result));
        Assert.IsType<ForbidResult>((await Controller(Owner()).ArchiveEvent(EventId, null, OtherTenant.ToString())).Result);
        Assert.Equal("Reason must not exceed 500 characters.",
            Message((await Controller(Owner()).ArchiveEvent(EventId, new ArchiveAuditEventRequest { Reason = new string('r', 501) }, null)).Result));
        _service.Verify(s => s.ArchiveEventAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Superseded ", "Superseded")]
    public async Task Archive_ReasonIsOptional_AndTrimmed(string? reason, string? expected)
    {
        await Controller(Owner()).ArchiveEvent(EventId, new ArchiveAuditEventRequest { Reason = reason }, null);

        _service.Verify(s => s.ArchiveEventAsync(EventId, Tenant, expected, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Archive_ReasonOfExactly500Chars_IsAccepted()
    {
        await Controller(Owner()).ArchiveEvent(EventId, new ArchiveAuditEventRequest { Reason = new string('r', 500) }, null);

        _service.Verify(s => s.ArchiveEventAsync(EventId, Tenant, new string('r', 500), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Archive_ReturnsTheResult_OrNotFound()
    {
        var archived = new AuditEventResponse();
        var missing = Guid.NewGuid();

        Assert.Equal($"Audit event with ID '{missing}' was not found for tenant '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>((await Controller(Owner()).ArchiveEvent(missing, null, null)).Result)));
        Assert.Equal($"Audit event with ID '{EventId}' was not found for tenant '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>((await Controller(Owner()).ArchiveEvent(EventId, null, null)).Result)));

        _service.Setup(s => s.ArchiveEventAsync(EventId, Tenant, null, It.IsAny<string>())).ReturnsAsync(archived);
        Assert.Same(archived, Assert.IsType<OkObjectResult>((await Controller(Owner()).ArchiveEvent(EventId, null, null)).Result).Value);
    }

    [Fact]
    public async Task Archive_ServiceErrors_MapToBadRequestAndConflict()
    {
        _service.SetupSequence(s => s.ArchiveEventAsync(EventId, Tenant, It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(new ArgumentException("Bad."))
            .ThrowsAsync(new AuditChainConflictException("Conflict."));

        Assert.Equal("Bad.", Message(Assert.IsType<BadRequestObjectResult>((await Controller(Owner()).ArchiveEvent(EventId, null, null)).Result)));
        Assert.Equal("Conflict.", Message(Assert.IsType<ConflictObjectResult>((await Controller(Owner()).ArchiveEvent(EventId, null, null)).Result)));
    }

    [Fact]
    public async Task Archive_StaffNotAssignedOrUnconfirmed_IsHidden()
    {
        Assert.Equal($"Audit event with ID '{EventId}' was not found for tenant '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>((await Controller(Staff()).ArchiveEvent(EventId, null, null)).Result)));

        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new EngagementAccessUnavailableException("down"));
        Assert.Equal(503, Assert.IsType<ObjectResult>((await Controller(Staff()).ArchiveEvent(EventId, null, null)).Result).StatusCode);
        _service.Verify(s => s.ArchiveEventAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
    }

    // ==========================================
    // Get by id
    // ==========================================

    [Fact]
    public async Task GetById_Guards_AndNotFound()
    {
        var missing = Guid.NewGuid();

        Assert.Equal(TenantRequired, Message((await Controller([new Claim(ClaimTypes.Role, "Owner")]).GetEventById(EventId, null)).Result));
        Assert.IsType<ForbidResult>((await Controller(Owner()).GetEventById(EventId, OtherTenant.ToString())).Result);
        Assert.Equal($"Audit event with ID '{missing}' was not found for tenant '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>((await Controller(Owner()).GetEventById(missing, null)).Result)));
    }

    [Fact]
    public async Task GetById_StaffNotAssigned_IsTheEventNotFoundMessage_AndUnconfirmedIs503()
    {
        Assert.Equal($"Audit event with ID '{EventId}' was not found for tenant '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>((await Controller(Staff()).GetEventById(EventId, null)).Result)));

        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new EngagementAccessUnavailableException("down"));
        Assert.Equal(503, Assert.IsType<ObjectResult>((await Controller(Staff()).GetEventById(EventId, null)).Result).StatusCode);
    }

    [Fact]
    public async Task GetById_StaffAssigned_OrOwnerOrStaffWhoIsOwner_SeeTheEvent()
    {
        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Assert.IsType<OkObjectResult>((await Controller(Staff()).GetEventById(EventId, null)).Result);
        Assert.IsType<OkObjectResult>((await Controller(Owner(), withAccess: false).GetEventById(EventId, null)).Result);
        Assert.IsType<OkObjectResult>((await Controller(Staff(new Claim(ClaimTypes.Role, "Owner")), withAccess: false).GetEventById(EventId, null)).Result);
    }

    // ==========================================
    // List for Staff
    // ==========================================

    [Fact]
    public async Task GetEvents_StaffWithoutAccessClientOrWhenUnreachable_Is503()
    {
        Assert.Equal(503, Assert.IsType<ObjectResult>((await Controller(Staff(), withAccess: false).GetEvents(null)).Result).StatusCode);

        _access.Setup(a => a.GetAccessibleEngagementIdsAsync(Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new EngagementAccessUnavailableException("down"));
        Assert.Equal(503, Assert.IsType<ObjectResult>((await Controller(Staff()).GetEvents(null)).Result).StatusCode);
    }
}
