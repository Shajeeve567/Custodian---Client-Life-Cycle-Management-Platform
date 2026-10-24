using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Stall;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// ClientActionsController, RequirementsController and ClientPortalController each carry their own copy of
/// the tenant, client-id and actor resolution rules (see the note on RequirementsController). The same
/// table of cases runs against each copy, through one simple endpoint per controller.
/// </summary>
public class ControllerIdentityResolutionTests
{
    private const string Tenant = "tenant-a";
    private const string TenantRequired = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter.";
    private static readonly Guid EngagementId = Guid.NewGuid();

    private readonly Mock<IClientActionService> _actions = new();
    private readonly Mock<IRequirementService> _requirements = new();
    private readonly Mock<IClientPortalService> _portal = new();
    private readonly Mock<IEngagementRepository> _engagements = new();

    public ControllerIdentityResolutionTests()
    {
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, It.IsAny<string>()))
            .ReturnsAsync(new Engagement { EngagementId = EngagementId, Status = EngagementStatus.Started });
        _actions.Setup(s => s.ClientOwnsEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
    }

    private static ControllerContext Context(Claim[] claims, string? tenantHeader = null, string? clientHeader = null)
    {
        var context = ContextFor(claims);
        if (tenantHeader is not null) context.HttpContext.Request.Headers["X-Tenant-ID"] = tenantHeader;
        if (clientHeader is not null) context.HttpContext.Request.Headers["X-Client-ID"] = clientHeader;
        return context;
    }

    private ClientActionsController Actions(Claim[] claims, string? tenantHeader = null) =>
        new(_actions.Object, Mock.Of<IStallActionsProvider>(), Mock.Of<IStallRecorder>(), Mock.Of<IDocumentComplianceClient>(),
            _engagements.Object, NullLogger<ClientActionsController>.Instance)
        { ControllerContext = Context(claims, tenantHeader) };

    private RequirementsController Requirements(Claim[] claims, string? tenantHeader = null) =>
        new(_requirements.Object, _engagements.Object, NullLogger<RequirementsController>.Instance)
        { ControllerContext = Context(claims, tenantHeader) };

    private ClientPortalController Portal(Claim[] claims, string? tenantHeader = null, string? clientHeader = null) =>
        new(_portal.Object, NullLogger<ClientPortalController>.Instance)
        { ControllerContext = Context(claims, tenantHeader, clientHeader) };

    // ==========================================
    // Tenant resolution: JWT first, then X-Tenant-ID, then the query string
    // ==========================================

    /// <summary>Expected: a tenant id, "forbid" or "bad-request".</summary>
    public static TheoryData<string, Claim[], string?, string?, string> TenantCases => new()
    {
        { "jwt trimmed", [new Claim("tenant_id", " tenant-a ")], null, null, Tenant },
        { "camelCase claim", [new Claim("tenantId", Tenant)], null, null, Tenant },
        { "snake_case wins", [new Claim("tenant_id", Tenant), new Claim("tenantId", "other")], null, null, Tenant },
        { "query mismatch", [new Claim("tenant_id", Tenant)], null, "tenant-b", "forbid" },
        { "query matches ignoring case", [new Claim("tenant_id", Tenant)], null, " TENANT-A ", Tenant },
        { "header mismatch", [new Claim("tenant_id", Tenant)], "tenant-b", null, "forbid" },
        { "header matches ignoring case", [new Claim("tenant_id", Tenant)], " Tenant-A ", null, Tenant },
        { "blank header ignored", [new Claim("tenant_id", Tenant)], "  ", null, Tenant },
        { "no jwt: header beats query", [], " tenant-a ", "other", Tenant },
        { "no jwt: blank header falls back to query", [], " ", " tenant-a ", Tenant },
        { "no jwt: query only", [], null, " tenant-a ", Tenant },
        { "nothing", [], null, null, "bad-request" },
        { "blank everything", [new Claim("tenant_id", " ")], " ", " ", "bad-request" }
    };

    private static void AssertTenantOutcome(string expected, IActionResult? result, string? tenantSeen)
    {
        switch (expected)
        {
            case "forbid":
                Assert.IsType<ForbidResult>(result);
                Assert.Null(tenantSeen);
                break;
            case "bad-request":
                Assert.IsType<BadRequestObjectResult>(result);
                Assert.Null(tenantSeen);
                break;
            default:
                Assert.IsType<OkObjectResult>(result);
                Assert.Equal(expected, tenantSeen);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(TenantCases))]
    public async Task ClientActions_ResolvesTheTenant(string _, Claim[] claims, string? header, string? query, string expected)
    {
        string? seen = null;
        _actions.Setup(s => s.PreviewStandardChecklistAsync(EngagementId, It.IsAny<string>()))
            .Callback((Guid _, string t) => seen = t).ReturnsAsync([]);

        var result = await Actions(claims, header).PreviewStandardChecklist(EngagementId, query);

        AssertTenantOutcome(expected, result.Result, seen);
        if (expected == "bad-request") Assert.Equal(TenantRequired, Message(result.Result));
    }

    [Theory]
    [MemberData(nameof(TenantCases))]
    public async Task Requirements_ResolvesTheTenant(string _, Claim[] claims, string? header, string? query, string expected)
    {
        string? seen = null;
        _requirements.Setup(s => s.GetRequirementsByEngagementAsync(EngagementId, It.IsAny<string>(), true, null))
            .Callback((Guid _, string t, bool _, string? _) => seen = t).ReturnsAsync([]);

        var result = await Requirements(claims, header).GetRequirements(EngagementId, query, isClientView: null);

        AssertTenantOutcome(expected, result.Result, seen);
        if (expected == "bad-request") Assert.Equal(TenantRequired, Message(result.Result));
    }

    [Theory]
    [MemberData(nameof(TenantCases))]
    public async Task Portal_ResolvesTheTenant(string _, Claim[] claims, string? header, string? query, string expected)
    {
        string? seen = null;
        _portal.Setup(s => s.GetDashboardForEngagementAsync(EngagementId, It.IsAny<string>(), null))
            .Callback((Guid _, string t, string? _) => seen = t).ReturnsAsync(new ClientPortalDashboardDto());

        var result = await Portal(claims, header).GetEngagementDashboard(EngagementId, query, clientId: null);

        AssertTenantOutcome(expected, result.Result, seen);
        if (expected == "bad-request") Assert.Equal(TenantRequired, Message(result.Result));
    }

    [Fact]
    public async Task GetStallStatus_NoTenant_HasItsOwnMessage()
    {
        var result = await Actions([]).GetStallStatus(EngagementId, null);

        Assert.Equal("Tenant identification is required.", Message(result.Result));
    }

    // ==========================================
    // A Client caller's own id: client_id, clientId, NameIdentifier, then sub
    // ==========================================

    public static TheoryData<Claim[], string?> CallerClientCases => new()
    {
        { [new Claim("client_id", " client-1 "), new Claim("clientId", "x"), new Claim(ClaimTypes.NameIdentifier, "y"), new Claim("sub", "z")], "client-1" },
        { [new Claim("clientId", "client-1"), new Claim(ClaimTypes.NameIdentifier, "y")], "client-1" },
        { [new Claim("client_id", " "), new Claim(ClaimTypes.NameIdentifier, " client-1 "), new Claim("sub", "z")], "client-1" },
        { [new Claim("sub", "client-1")], "client-1" },
        { [new Claim(ClaimTypes.NameIdentifier, " ")], null },
        { [], null }
    };

    private static Claim[] ClientCaller(Claim[] identity) => [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), .. identity];

    [Theory]
    [MemberData(nameof(CallerClientCases))]
    public async Task ClientActions_ResolvesTheCallingClient(Claim[] identity, string? expected)
    {
        _actions.Setup(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, true, null)).ReturnsAsync([]);

        var result = await Actions(ClientCaller(identity)).GetActionHistory(EngagementId, null, null, null);

        if (expected is null)
        {
            Assert.IsType<ForbidResult>(result.Result);
            _actions.Verify(s => s.ClientOwnsEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result.Result);
            _actions.Verify(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, expected), Times.Once);
        }
    }

    [Theory]
    [MemberData(nameof(CallerClientCases))]
    public async Task Requirements_ResolvesTheCallingClient(Claim[] identity, string? expected)
    {
        var result = await Requirements(ClientCaller(identity)).GetRequirements(EngagementId, null, null);

        if (expected is null)
        {
            Assert.IsType<ForbidResult>(result.Result);
            _requirements.VerifyNoOtherCalls();
        }
        else
        {
            _requirements.Verify(s => s.GetRequirementsByEngagementAsync(EngagementId, Tenant, true, expected), Times.Once);
        }
    }

    // ==========================================
    // Actor for the audit trail: NameIdentifier, sub, Name, then Email
    // ==========================================

    public static TheoryData<Claim[], string?> ActorCases => new()
    {
        { [new Claim(ClaimTypes.NameIdentifier, " staff-1 "), new Claim("sub", "s"), new Claim(ClaimTypes.Name, "n"), new Claim(ClaimTypes.Email, "e")], "staff-1" },
        { [new Claim("sub", "from-sub"), new Claim(ClaimTypes.Name, "n"), new Claim(ClaimTypes.Email, "e")], "from-sub" },
        { [new Claim(ClaimTypes.Name, "Jane Staff"), new Claim(ClaimTypes.Email, "e")], "Jane Staff" },
        { [new Claim(ClaimTypes.Email, "jane@example.com")], "jane@example.com" },
        { [new Claim(ClaimTypes.Email, "  ")], null },
        { [], null }
    };

    private static Claim[] StaffCaller(Claim[] identity) => [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), .. identity];

    [Theory]
    [MemberData(nameof(ActorCases))]
    public async Task ClientActions_StaffActorFromTheJwt_DefaultsToStaff(Claim[] identity, string? expected)
    {
        _actions.Setup(s => s.ApplyStandardChecklistAsync(EngagementId, Tenant, It.IsAny<string>())).ReturnsAsync([]);

        await Actions(StaffCaller(identity)).ApplyStandardChecklist(EngagementId, null);

        _actions.Verify(s => s.ApplyStandardChecklistAsync(EngagementId, Tenant, expected ?? "Staff"), Times.Once);
    }

    [Theory]
    [MemberData(nameof(ActorCases))]
    public async Task Requirements_ActorFromTheJwt_KeepsTheBodyValueOnlyWhenTheJwtHasNone(Claim[] identity, string? expected)
    {
        var dto = new RequestRequirementDto { Type = "Text", RequestedByActor = "from-body" };

        await Requirements(StaffCaller(identity)).RequestRequirement(EngagementId, dto, null);

        Assert.Equal(expected ?? "from-body", dto.RequestedByActor);
    }

    [Fact]
    public async Task Requirements_Review_ActorFallsBackToEmpty_WhenNeitherJwtNorBodyHasOne()
    {
        var dto = new ReviewRequirementDto { ReviewerActor = null };

        await Requirements(StaffCaller([])).ReviewRequirement(EngagementId, Guid.NewGuid(), dto, null);

        Assert.Equal(string.Empty, dto.ReviewerActor);
    }

    // ==========================================
    // Staff authorization: Owner/Staff roles (any case), and unauthenticated test callers pass
    // ==========================================

    public static TheoryData<Claim[], string?, bool> StaffAuthCases => new()
    {
        { [new Claim(ClaimTypes.Role, "Owner")], "test", true },
        { [new Claim(ClaimTypes.Role, "Staff")], "test", true },
        { [new Claim(ClaimTypes.Role, "staff")], "test", true },
        { [new Claim(ClaimTypes.Role, "OWNER")], "test", true },
        { [new Claim(ClaimTypes.Role, "Client")], "test", false },
        { [new Claim("role", "Staff")], "test", false },
        { [], "test", false },
        { [new Claim(ClaimTypes.Role, "Client")], null, true }
    };

    private static ControllerContext AuthContext(Claim[] claims, string? authType)
    {
        var context = new ControllerContext { HttpContext = new DefaultHttpContext() };
        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Tenant), .. claims], authType));
        return context;
    }

    [Theory]
    [MemberData(nameof(StaffAuthCases))]
    public async Task ClientActions_StaffAuthorization(Claim[] claims, string? authType, bool allowed)
    {
        _actions.Setup(s => s.ApplyStandardChecklistAsync(EngagementId, Tenant, It.IsAny<string>())).ReturnsAsync([]);
        var controller = Actions([]);
        controller.ControllerContext = AuthContext(claims, authType);

        var result = await controller.ApplyStandardChecklist(EngagementId, null);

        if (allowed) Assert.IsType<OkObjectResult>(result.Result);
        else Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [MemberData(nameof(StaffAuthCases))]
    public async Task Requirements_StaffAuthorization(Claim[] claims, string? authType, bool allowed)
    {
        _requirements.Setup(s => s.RequestRequirementAsync(EngagementId, Tenant, It.IsAny<RequestRequirementDto>())).ReturnsAsync(new RequirementResponseDto());
        var controller = Requirements([]);
        controller.ControllerContext = AuthContext(claims, authType);

        var result = await controller.RequestRequirement(EngagementId, new RequestRequirementDto { Type = "Text" }, null);

        if (allowed) Assert.IsType<CreatedAtActionResult>(result.Result);
        else Assert.IsType<ForbidResult>(result.Result);
    }

    // ==========================================
    // Client portal: which client the dashboard is for
    // ==========================================

    private void ActiveDashboardFor(string client) =>
        _portal.Setup(s => s.GetActiveDashboardForClientAsync(Tenant, client, It.IsAny<string?>())).ReturnsAsync(new ClientPortalDashboardDto());

    public static TheoryData<string, Claim[], string?, string?, string> PortalClientCases => new()
    {
        // Expected: the client id the service is asked for, "forbid", "staff-preview" or "bad-request".
        { "client_id claim trimmed", [new Claim("client_id", " client-1 ")], null, null, "client-1" },
        { "clientId claim", [new Claim("clientId", "client-1"), new Claim(ClaimTypes.NameIdentifier, "x")], null, null, "client-1" },
        { "client_id wins over clientId", [new Claim("client_id", "client-1"), new Claim("clientId", "x")], null, null, "client-1" },
        { "client role uses NameIdentifier", [new Claim(ClaimTypes.Role, "Client"), new Claim(ClaimTypes.NameIdentifier, "client-1"), new Claim("sub", "x")], null, null, "client-1" },
        { "client role uses sub", [new Claim(ClaimTypes.Role, "Client"), new Claim("sub", "client-1")], null, null, "client-1" },
        { "query mismatch", [new Claim("client_id", "client-1")], "client-2", null, "forbid" },
        { "query matches ignoring case", [new Claim("client_id", "client-1")], " CLIENT-1 ", null, "client-1" },
        { "header mismatch", [new Claim("client_id", "client-1")], null, "client-2", "forbid" },
        { "header matches ignoring case", [new Claim("client_id", "client-1")], null, " Client-1 ", "client-1" },
        { "blank header ignored", [new Claim("client_id", "client-1")], null, " ", "client-1" },
        { "no claim: query trimmed", [], " client-1 ", "x", "client-1" },
        { "no claim: header trimmed", [], null, " client-1 ", "client-1" },
        { "no claim: blank header, no role: NameIdentifier", [new Claim(ClaimTypes.NameIdentifier, " client-1 ")], null, " ", "client-1" },
        { "no claim, no role: sub", [new Claim("sub", "client-1")], null, null, "client-1" },
        { "no claim, no role: NameIdentifier wins over sub", [new Claim(ClaimTypes.NameIdentifier, "client-1"), new Claim("sub", "x")], null, null, "client-1" },
        { "client role NameIdentifier cannot be overridden by query", [new Claim(ClaimTypes.Role, "Client"), new Claim(ClaimTypes.NameIdentifier, "client-1")], "client-2", null, "forbid" },
        { "client role sub cannot be overridden by query", [new Claim(ClaimTypes.Role, "Client"), new Claim("sub", "client-1")], "client-2", null, "forbid" },
        { "staff never uses own id", [new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1")], null, null, "staff-preview" },
        { "owner never uses own id", [new Claim(ClaimTypes.Role, "Owner"), new Claim("sub", "owner-1")], null, null, "staff-preview" },
        { "nobody", [], null, null, "bad-request" }
    };

    [Theory]
    [MemberData(nameof(PortalClientCases))]
    public async Task Portal_MyEngagement_ResolvesTheClient(string _, Claim[] identity, string? query, string? header, string expected)
    {
        ActiveDashboardFor("client-1");
        _portal.Setup(s => s.GetActiveDashboardForTenantAsync(Tenant, It.IsAny<string?>())).ReturnsAsync(new ClientPortalDashboardDto());

        var result = await Portal([new Claim("tenant_id", Tenant), .. identity], clientHeader: header).GetMyActiveEngagement(null, query);

        switch (expected)
        {
            case "forbid":
                Assert.IsType<ForbidResult>(result.Result);
                _portal.VerifyNoOtherCalls();
                break;
            case "bad-request":
                Assert.Equal("Client identification is required via JWT sub/clientId claim, X-Client-ID header, or clientId parameter.",
                    Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
                _portal.VerifyNoOtherCalls();
                break;
            case "staff-preview":
                Assert.IsType<OkObjectResult>(result.Result);
                _portal.Verify(s => s.GetActiveDashboardForTenantAsync(Tenant, It.IsAny<string?>()), Times.Once);
                _portal.Verify(s => s.GetActiveDashboardForClientAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
                break;
            default:
                Assert.IsType<OkObjectResult>(result.Result);
                _portal.Verify(s => s.GetActiveDashboardForClientAsync(Tenant, expected, It.IsAny<string?>()), Times.Once);
                break;
        }
    }

    [Fact]
    public async Task Portal_MyEngagement_StaffPreview_IsRestrictedToTheirEngagements_AndNotFoundHasMessage()
    {
        var result = await Portal([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1")])
            .GetMyActiveEngagement(null, null);

        Assert.Equal($"No active onboarding engagement found in workspace '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
        _portal.Verify(s => s.GetActiveDashboardForTenantAsync(Tenant, "staff-1"), Times.Once);
    }

    [Fact]
    public async Task Portal_MyEngagement_StaffPreviewOfAClient_IsRestrictedToTheirEngagements()
    {
        ActiveDashboardFor("client-1");

        await Portal([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1")])
            .GetMyActiveEngagement(null, "client-1");

        _portal.Verify(s => s.GetActiveDashboardForClientAsync(Tenant, "client-1", "staff-1"), Times.Once);
    }

    [Fact]
    public async Task Portal_MyEngagement_ClientWithoutActiveEngagement_IsNotFoundWithMessage()
    {
        var result = await Portal([new Claim("tenant_id", Tenant), new Claim("client_id", "client-1")]).GetMyActiveEngagement(null, null);

        Assert.Equal($"No active onboarding engagement found for client 'client-1' in workspace '{Tenant}'.",
            Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Portal_MyEngagement_TenantChecks()
    {
        Assert.IsType<ForbidResult>((await Portal([new Claim("tenant_id", Tenant)]).GetMyActiveEngagement("tenant-b", null)).Result);
        Assert.Equal(TenantRequired, Message((await Portal([]).GetMyActiveEngagement(null, "client-1")).Result));
        _portal.VerifyNoOtherCalls();
    }

    // ==========================================
    // Client portal: one engagement's dashboard
    // ==========================================

    [Fact]
    public async Task Portal_Dashboard_ClientMismatch_IsForbidden()
    {
        var result = await Portal([new Claim("tenant_id", Tenant), new Claim("client_id", "client-1")]).GetEngagementDashboard(EngagementId, null, "client-2");

        Assert.IsType<ForbidResult>(result.Result);
        _portal.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Portal_Dashboard_ClientIsConstrainedToOwnEngagement()
    {
        _portal.Setup(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, "client-1")).ReturnsAsync(new ClientPortalDashboardDto());

        var result = await Portal([new Claim("tenant_id", Tenant), new Claim("client_id", "client-1")]).GetEngagementDashboard(EngagementId, null, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Portal_Dashboard_AnotherClientsEngagement_Is403WithMessage()
    {
        _portal.Setup(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, null)).ReturnsAsync(new ClientPortalDashboardDto());

        var result = await Portal([new Claim("tenant_id", Tenant), new Claim("client_id", "client-1")]).GetEngagementDashboard(EngagementId, null, null);

        var denied = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal("Access denied: Clients can only access their own engagement.", Message(denied));
    }

    [Fact]
    public async Task Portal_Dashboard_UnknownEngagement_IsNotFoundWithMessage()
    {
        var result = await Portal([new Claim("tenant_id", Tenant), new Claim("client_id", "client-1")]).GetEngagementDashboard(EngagementId, null, null);

        Assert.Equal($"Engagement '{EngagementId}' was not found in tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
        _portal.Verify(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, null), Times.Once);
    }

    [Fact]
    public async Task Portal_Dashboard_StaffWithoutClient_SeesAnyEngagement_AndMissingIsPlainNotFound()
    {
        var staff = () => Portal([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1")]);

        var missing = await staff().GetEngagementDashboard(EngagementId, null, null);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
        _portal.Verify(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, null), Times.Once);

        _portal.Setup(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, null)).ReturnsAsync(new ClientPortalDashboardDto());
        Assert.IsType<OkObjectResult>((await staff().GetEngagementDashboard(EngagementId, null, null)).Result);
    }

    [Fact]
    public async Task Portal_Dashboard_ClientRoleWithoutId_IsNotConstrainedToANullClient_ButStillChecked()
    {
        var result = await Portal([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client")]).GetEngagementDashboard(EngagementId, null, null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        _portal.Verify(s => s.GetDashboardForEngagementAsync(EngagementId, Tenant, null), Times.Once);
        _portal.VerifyNoOtherCalls();
    }
}
