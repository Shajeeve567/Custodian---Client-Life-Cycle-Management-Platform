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
/// EngagementConditionsController paths not covered by <see cref="EngagementConditionsControllerTests"/>:
/// tenant resolution (JWT, X-Tenant-ID header, query), staff vs client reads, actor fallbacks,
/// terminal engagements and how each service exception maps to an HTTP status.
/// </summary>
public class EngagementConditionsControllerAccessTests
{
    private const string Tenant = "tenant-a";
    private const string TenantRequired = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter.";
    private static readonly Guid EngagementId = Guid.NewGuid();
    private static readonly Guid ConditionId = Guid.NewGuid();

    private readonly Mock<IConditionService> _conditions = new();
    private readonly Mock<IClientActionService> _clientActions = new();
    private readonly Mock<IEngagementRepository> _engagements = new();

    public EngagementConditionsControllerAccessTests()
    {
        WithEngagement(EngagementStatus.Started);
        _clientActions.Setup(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-1")).ReturnsAsync(true);
        _conditions.Setup(s => s.AttachConditionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AttachConditionDto>(), It.IsAny<string>()))
            .ReturnsAsync(new ConditionResponseDto { ConditionId = ConditionId });
    }

    private void WithEngagement(EngagementStatus? status) =>
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, Tenant))
            .ReturnsAsync(status is null ? null : new Engagement { EngagementId = EngagementId, TenantId = Tenant, Status = status.Value });

    private EngagementConditionsController Controller(params Claim[] claims) =>
        new(_conditions.Object, _clientActions.Object, _engagements.Object, NullLogger<EngagementConditionsController>.Instance)
        {
            ControllerContext = ContextFor(claims)
        };

    private EngagementConditionsController Staff(params Claim[] extra) =>
        Controller([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1"), .. extra]);

    private EngagementConditionsController Client(params Claim[] identity) =>
        Controller([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), .. identity]);

    private EngagementConditionsController ClientOne() => Client(new Claim("client_id", "client-1"));

    private static EngagementConditionsController WithHeader(EngagementConditionsController controller, string value)
    {
        controller.HttpContext.Request.Headers["X-Tenant-ID"] = value;
        return controller;
    }

    private void StaffListReturns(bool includeInactive) =>
        _conditions.Setup(s => s.GetConditionsStaffAsync(EngagementId, Tenant, includeInactive)).ReturnsAsync([new ConditionResponseDto()]);

    // ==========================================
    // Tenant resolution
    // ==========================================

    [Fact]
    public async Task Tenant_HeaderDifferentFromJwt_IsForbidden()
    {
        var result = await WithHeader(Staff(), "tenant-b").GetConditions(EngagementId, null, null);

        Assert.IsType<ForbidResult>(result);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Tenant_HeaderMatchingJwtIgnoringCase_IsAllowed()
    {
        StaffListReturns(true);

        var result = await WithHeader(Staff(), " TENANT-A ").GetConditions(EngagementId, null, null);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Tenant_BlankHeaderWithJwt_IsIgnored()
    {
        StaffListReturns(true);

        var result = await WithHeader(Staff(), "  ").GetConditions(EngagementId, null, null);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Tenant_JwtIsTrimmed_AndCamelCaseClaimAccepted()
    {
        StaffListReturns(true);

        var result = await Controller(new Claim("tenantId", " tenant-a "), new Claim(ClaimTypes.Role, "Owner")).GetConditions(EngagementId, null, null);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Tenant_SnakeCaseClaimWins_WhenBothArePresent()
    {
        StaffListReturns(true);

        var result = await Controller(new Claim("tenant_id", Tenant), new Claim("tenantId", "other"), new Claim(ClaimTypes.Role, "Staff"))
            .GetConditions(EngagementId, null, null);

        Assert.IsType<OkObjectResult>(result);
        _conditions.Verify(s => s.GetConditionsStaffAsync(EngagementId, Tenant, true), Times.Once);
    }

    [Fact]
    public async Task Tenant_QueryDifferentFromJwt_IsForbidden_QueryMatchingIgnoringCaseIsAllowed()
    {
        StaffListReturns(true);

        Assert.IsType<ForbidResult>(await Staff().GetConditions(EngagementId, "tenant-b", null));
        Assert.IsType<OkObjectResult>(await Staff().GetConditions(EngagementId, " Tenant-A ", null));
    }

    [Fact]
    public async Task Tenant_NoJwt_UsesTrimmedHeader_BeforeQuery()
    {
        StaffListReturns(true);

        var result = await WithHeader(Controller(new Claim(ClaimTypes.Role, "Staff")), " tenant-a ").GetConditions(EngagementId, "ignored", null);

        Assert.IsType<OkObjectResult>(result);
        _conditions.Verify(s => s.GetConditionsStaffAsync(EngagementId, Tenant, true), Times.Once);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Tenant_NoJwt_BlankHeader_FallsBackToTrimmedQuery()
    {
        StaffListReturns(true);

        var result = await WithHeader(Controller(new Claim(ClaimTypes.Role, "Staff")), " ").GetConditions(EngagementId, " tenant-a ", null);

        Assert.IsType<OkObjectResult>(result);
        _conditions.Verify(s => s.GetConditionsStaffAsync(EngagementId, Tenant, true), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Tenant_Nowhere_IsBadRequestOnEveryAction(string? query)
    {
        var c = Controller(new Claim(ClaimTypes.Role, "Staff"));

        Assert.Equal(TenantRequired, Message(await c.GetConditions(EngagementId, query, null)));
        Assert.Equal(TenantRequired, Message(await c.GetConditionById(EngagementId, ConditionId, query)));
        Assert.Equal(TenantRequired, Message((await c.AttachCondition(EngagementId, new AttachConditionDto(), query)).Result));
        Assert.Equal(TenantRequired, Message((await c.UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), query)).Result));
        Assert.Equal(TenantRequired, Message((await c.DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), query)).Result));
        Assert.Equal(TenantRequired, Message((await c.SatisfyCondition(EngagementId, ConditionId, query)).Result));
        Assert.Equal(TenantRequired, Message((await c.ApproveCondition(EngagementId, ConditionId, query)).Result));
        Assert.Equal(TenantRequired, Message((await c.RejectCondition(EngagementId, ConditionId, new RejectApprovalDto(), query)).Result));
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Tenant_Mismatch_IsForbiddenOnEveryAction()
    {
        var c = Staff();

        Assert.IsType<ForbidResult>(await c.GetConditionById(EngagementId, ConditionId, "tenant-b"));
        Assert.IsType<ForbidResult>((await c.UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await c.SatisfyCondition(EngagementId, ConditionId, "tenant-b")).Result);
        Assert.IsType<ForbidResult>((await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "x" }, "tenant-b")).Result);
        _conditions.VerifyNoOtherCalls();
    }

    // ==========================================
    // Reads: staff vs client
    // ==========================================

    [Fact]
    public async Task GetConditions_Staff_IncludesInactiveByDefault()
    {
        StaffListReturns(true);

        var result = await Staff().GetConditions(EngagementId, null, includeInactive: null);

        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ConditionResponseDto>>(Assert.IsType<OkObjectResult>(result).Value));
        _conditions.Verify(s => s.GetConditionsStaffAsync(EngagementId, Tenant, true), Times.Once);
    }

    [Fact]
    public async Task GetConditions_Staff_CanExcludeInactive()
    {
        StaffListReturns(false);

        await Staff().GetConditions(EngagementId, null, includeInactive: false);

        _conditions.Verify(s => s.GetConditionsStaffAsync(EngagementId, Tenant, false), Times.Once);
    }

    [Fact]
    public async Task GetConditions_NoRecognisedRole_IsForbidden()
    {
        var result = await Controller(new Claim("tenant_id", Tenant)).GetConditions(EngagementId, null, null);

        Assert.IsType<ForbidResult>(result);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetConditions_ClientWithoutIdentity_IsForbidden()
    {
        var result = await Client().GetConditions(EngagementId, null, null);

        Assert.IsType<ForbidResult>(result);
        _clientActions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("client_id")]
    [InlineData("clientId")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task GetConditions_ClientIdentity_ComesFromClaims_Trimmed(string claimType)
    {
        _conditions.Setup(s => s.GetConditionsClientAsync(EngagementId, Tenant, "client-1")).ReturnsAsync([new ClientSafeConditionDto()]);

        var result = await Client(new Claim(claimType, " client-1 ")).GetConditions(EngagementId, null, null);

        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientSafeConditionDto>>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task GetConditions_ClientIdClaimsWinOverSubject()
    {
        await Client(new Claim("client_id", "client-1"), new Claim("clientId", "x"), new Claim(ClaimTypes.NameIdentifier, "y"))
            .GetConditions(EngagementId, null, null);

        _clientActions.Verify(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-1"), Times.Once);
    }

    [Fact]
    public async Task GetConditions_BlankClientIdClaim_FallsBackToSubject()
    {
        await Client(new Claim("client_id", " "), new Claim(ClaimTypes.NameIdentifier, "client-1"), new Claim("sub", "other"))
            .GetConditions(EngagementId, null, null);

        _clientActions.Verify(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-1"), Times.Once);
    }

    [Fact]
    public async Task GetConditionById_Staff_FoundAndMissing()
    {
        var condition = new ConditionResponseDto { ConditionId = ConditionId };
        _conditions.Setup(s => s.GetConditionByIdStaffAsync(EngagementId, ConditionId, Tenant)).ReturnsAsync(condition);

        Assert.Same(condition, Assert.IsType<OkObjectResult>(await Staff().GetConditionById(EngagementId, ConditionId, null)).Value);

        var missing = Guid.NewGuid();
        var notFound = await Staff().GetConditionById(EngagementId, missing, null);
        Assert.IsType<NotFoundObjectResult>(notFound);
        Assert.Equal($"Condition '{missing}' was not found.", Message(notFound));
    }

    [Fact]
    public async Task GetConditionById_Client_FoundAndMissing()
    {
        var condition = new ClientSafeConditionDto { ConditionId = ConditionId };
        _conditions.Setup(s => s.GetConditionByIdClientAsync(EngagementId, ConditionId, Tenant, "client-1")).ReturnsAsync(condition);

        Assert.Same(condition, Assert.IsType<OkObjectResult>(await ClientOne().GetConditionById(EngagementId, ConditionId, null)).Value);

        var missing = Guid.NewGuid();
        var notFound = await ClientOne().GetConditionById(EngagementId, missing, null);
        Assert.IsType<NotFoundObjectResult>(notFound);
        Assert.Equal($"Condition '{missing}' was not found.", Message(notFound));
    }

    [Fact]
    public async Task GetConditionById_ClientChecks_AndNoRole()
    {
        Assert.IsType<ForbidResult>(await Client().GetConditionById(EngagementId, ConditionId, null));
        Assert.IsType<ForbidResult>(await Client(new Claim("client_id", "client-2")).GetConditionById(EngagementId, ConditionId, null));
        Assert.IsType<ForbidResult>(await Controller(new Claim("tenant_id", Tenant)).GetConditionById(EngagementId, ConditionId, null));
        _conditions.VerifyNoOtherCalls();
        _clientActions.Verify(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-2"), Times.Once);
        _clientActions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientDecisions_StaffCaller_IsForbidden_EvenWithAUserId()
    {
        Assert.IsType<ForbidResult>((await Staff().ApproveCondition(EngagementId, ConditionId, null)).Result);
        Assert.IsType<ForbidResult>((await Staff().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result);
        _engagements.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _conditions.VerifyNoOtherCalls();
    }

    // ==========================================
    // Staff mutations: attach, update, deactivate, satisfy
    // ==========================================

    public static TheoryData<Exception, Type> StaffErrors => new()
    {
        { new KeyNotFoundException("Condition not found."), typeof(NotFoundObjectResult) },
        { new InvalidOperationException("Condition is already satisfied."), typeof(ConflictObjectResult) },
        { new ArgumentException("Amount must be positive."), typeof(BadRequestObjectResult) }
    };

    [Theory]
    [MemberData(nameof(StaffErrors))]
    public async Task StaffMutations_MapServiceExceptions(Exception error, Type expected)
    {
        _conditions.Setup(s => s.AttachConditionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AttachConditionDto>(), It.IsAny<string>())).ThrowsAsync(error);
        _conditions.Setup(s => s.UpdateConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpdateConditionDto>(), It.IsAny<string>())).ThrowsAsync(error);
        _conditions.Setup(s => s.DeactivateConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeactivateConditionDto>(), It.IsAny<string>())).ThrowsAsync(error);
        _conditions.Setup(s => s.SatisfyConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(error);

        var results = new IActionResult?[]
        {
            (await Staff().AttachCondition(EngagementId, new AttachConditionDto(), null)).Result,
            (await Staff().UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null)).Result,
            (await Staff().DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null)).Result,
            (await Staff().SatisfyCondition(EngagementId, ConditionId, null)).Result
        };

        Assert.All(results, r =>
        {
            Assert.IsType(expected, r);
            Assert.Equal(error.Message, Message(r));
        });
    }

    [Fact]
    public async Task StaffMutations_ClientCaller_IsForbidden()
    {
        var c = ClientOne();

        Assert.IsType<ForbidResult>((await c.UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null)).Result);
        Assert.IsType<ForbidResult>((await c.DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null)).Result);
        Assert.IsType<ForbidResult>((await c.SatisfyCondition(EngagementId, ConditionId, null)).Result);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaffMutations_InvalidModel_IsBadRequest_BeforeLoadingEngagement()
    {
        var c = Staff();
        c.ModelState.AddModelError("Reason", "Required");

        Assert.IsType<BadRequestObjectResult>((await c.AttachCondition(EngagementId, new AttachConditionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null)).Result);
        _engagements.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement is closed and cannot accept this change.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot accept this change.")]
    public async Task Mutations_OnTerminalEngagement_AreConflict(EngagementStatus status, string message)
    {
        WithEngagement(status);

        var results = new IActionResult?[]
        {
            (await Staff().UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null)).Result,
            (await Staff().DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null)).Result,
            (await ClientOne().ApproveCondition(EngagementId, ConditionId, null)).Result,
            (await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result
        };

        Assert.All(results, r =>
        {
            Assert.IsType<ConflictObjectResult>(r);
            Assert.Equal(message, Message(r));
        });
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mutations_OnMissingEngagement_AreNotFound()
    {
        WithEngagement(null);

        Assert.IsType<NotFoundResult>((await Staff().AttachCondition(EngagementId, new AttachConditionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await Staff().UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await Staff().DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await ClientOne().ApproveCondition(EngagementId, ConditionId, null)).Result);
        Assert.IsType<NotFoundResult>((await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Attach_ReturnsCreatedAtGetConditionById_WithRouteValues()
    {
        var result = await Staff().AttachCondition(EngagementId, new AttachConditionDto(), null);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(EngagementConditionsController.GetConditionById), created.ActionName);
        Assert.Equal(EngagementId, created.RouteValues!["engagementId"]);
        Assert.Equal(ConditionId, created.RouteValues["conditionId"]);
        Assert.Equal(Tenant, created.RouteValues["tenantId"]);
    }

    [Fact]
    public async Task Update_Deactivate_Satisfy_ReturnTheServiceResult_WithTheActor()
    {
        var updated = new ConditionResponseDto();
        var deactivated = new ConditionResponseDto();
        var satisfied = new ConditionResponseDto();
        var updateDto = new UpdateConditionDto();
        var deactivateDto = new DeactivateConditionDto { Reason = "No longer needed" };
        _conditions.Setup(s => s.UpdateConditionAsync(EngagementId, ConditionId, Tenant, updateDto, "staff-1")).ReturnsAsync(updated);
        _conditions.Setup(s => s.DeactivateConditionAsync(EngagementId, ConditionId, Tenant, deactivateDto, "staff-1")).ReturnsAsync(deactivated);
        _conditions.Setup(s => s.SatisfyConditionAsync(EngagementId, ConditionId, Tenant, "staff-1")).ReturnsAsync(satisfied);

        Assert.Same(updated, Assert.IsType<OkObjectResult>((await Staff().UpdateCondition(EngagementId, ConditionId, updateDto, null)).Result).Value);
        Assert.Same(deactivated, Assert.IsType<OkObjectResult>((await Staff().DeactivateCondition(EngagementId, ConditionId, deactivateDto, null)).Result).Value);
        Assert.Same(satisfied, Assert.IsType<OkObjectResult>((await Staff().SatisfyCondition(EngagementId, ConditionId, null)).Result).Value);
    }

    public static TheoryData<Claim[], string> StaffActors => new()
    {
        { [new Claim(ClaimTypes.NameIdentifier, " staff-1 "), new Claim("sub", "s"), new Claim(ClaimTypes.Name, "n"), new Claim(ClaimTypes.Email, "e")], "staff-1" },
        { [new Claim("sub", "from-sub"), new Claim(ClaimTypes.Name, "n"), new Claim(ClaimTypes.Email, "e")], "from-sub" },
        { [new Claim(ClaimTypes.Name, "Jane Staff"), new Claim(ClaimTypes.Email, "e")], "Jane Staff" },
        { [new Claim(ClaimTypes.Email, "jane@example.com")], "jane@example.com" },
        { [new Claim(ClaimTypes.Email, "   ")], "staff-user" },
        { [], "staff-user" }
    };

    [Theory]
    [MemberData(nameof(StaffActors))]
    public async Task Attach_Actor_FollowsTheClaimFallbackChain(Claim[] identity, string expectedActor)
    {
        await Controller([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), .. identity])
            .AttachCondition(EngagementId, new AttachConditionDto(), null);

        _conditions.Verify(s => s.AttachConditionAsync(EngagementId, Tenant, It.IsAny<AttachConditionDto>(), expectedActor), Times.Once);
    }

    [Fact]
    public async Task UpdateDeactivateSatisfy_WithoutIdentity_UseStaffUserActor()
    {
        var owner = () => Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"));

        await owner().UpdateCondition(EngagementId, ConditionId, new UpdateConditionDto(), null);
        await owner().DeactivateCondition(EngagementId, ConditionId, new DeactivateConditionDto(), null);
        await owner().SatisfyCondition(EngagementId, ConditionId, null);

        _conditions.Verify(s => s.UpdateConditionAsync(EngagementId, ConditionId, Tenant, It.IsAny<UpdateConditionDto>(), "staff-user"), Times.Once);
        _conditions.Verify(s => s.DeactivateConditionAsync(EngagementId, ConditionId, Tenant, It.IsAny<DeactivateConditionDto>(), "staff-user"), Times.Once);
        _conditions.Verify(s => s.SatisfyConditionAsync(EngagementId, ConditionId, Tenant, "staff-user"), Times.Once);
    }

    // ==========================================
    // Client approvals: approve, reject
    // ==========================================

    public static TheoryData<Exception, Type> ClientErrors => new()
    {
        { new KeyNotFoundException("Condition not found."), typeof(NotFoundObjectResult) },
        { new InvalidOperationException("Already decided."), typeof(ConflictObjectResult) },
        { new ArgumentException("Bad input."), typeof(BadRequestObjectResult) }
    };

    [Theory]
    [MemberData(nameof(ClientErrors))]
    public async Task ClientDecisions_MapServiceExceptions(Exception error, Type expected)
    {
        _conditions.Setup(s => s.ApproveConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(error);
        _conditions.Setup(s => s.RejectConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RejectApprovalDto>(), It.IsAny<string>())).ThrowsAsync(error);

        var approve = (await ClientOne().ApproveCondition(EngagementId, ConditionId, null)).Result;
        var reject = (await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result;

        Assert.IsType(expected, approve);
        Assert.Equal(error.Message, Message(approve));
        Assert.IsType(expected, reject);
        Assert.Equal(error.Message, Message(reject));
    }

    [Fact]
    public async Task ClientDecisions_UnauthorizedAccess_IsForbidden()
    {
        _conditions.Setup(s => s.ApproveConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        _conditions.Setup(s => s.RejectConditionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RejectApprovalDto>(), It.IsAny<string>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        Assert.IsType<ForbidResult>((await ClientOne().ApproveCondition(EngagementId, ConditionId, null)).Result);
        Assert.IsType<ForbidResult>((await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result);
    }

    [Fact]
    public async Task ClientDecisions_ClientWithoutIdentity_IsForbidden()
    {
        Assert.IsType<ForbidResult>((await Client().ApproveCondition(EngagementId, ConditionId, null)).Result);
        Assert.IsType<ForbidResult>((await Client().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result);
        _engagements.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClientDecisions_ActorIsClientId_WhenNoSubjectClaims()
    {
        var approved = new ClientSafeConditionDto();
        var rejected = new ClientSafeConditionDto();
        _conditions.Setup(s => s.ApproveConditionAsync(EngagementId, ConditionId, Tenant, "client-1", "client-1")).ReturnsAsync(approved);
        _conditions.Setup(s => s.RejectConditionAsync(EngagementId, ConditionId, Tenant, "client-1", It.IsAny<RejectApprovalDto>(), "client-1")).ReturnsAsync(rejected);

        Assert.Same(approved, Assert.IsType<OkObjectResult>((await ClientOne().ApproveCondition(EngagementId, ConditionId, null)).Result).Value);
        Assert.Same(rejected, Assert.IsType<OkObjectResult>((await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result).Value);
    }

    [Fact]
    public async Task ClientDecisions_ActorIsSubject_WhenPresent()
    {
        var c = () => Client(new Claim("client_id", "client-1"), new Claim("sub", "user-7"));

        await c().ApproveCondition(EngagementId, ConditionId, null);
        await c().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null);

        _conditions.Verify(s => s.ApproveConditionAsync(EngagementId, ConditionId, Tenant, "client-1", "user-7"), Times.Once);
        _conditions.Verify(s => s.RejectConditionAsync(EngagementId, ConditionId, Tenant, "client-1", It.IsAny<RejectApprovalDto>(), "user-7"), Times.Once);
    }

    [Fact]
    public async Task Reject_NullDtoOrInvalidModel_IsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>((await ClientOne().RejectCondition(EngagementId, ConditionId, null!, null)).Result);

        var invalid = ClientOne();
        invalid.ModelState.AddModelError("Reason", "Required");
        Assert.IsType<BadRequestObjectResult>((await invalid.RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "No" }, null)).Result);
        _conditions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reject_ReasonOfExactly500CharsAfterTrim_IsAccepted()
    {
        var reason = "  " + new string('r', 500) + "  ";

        var result = await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = reason }, null);

        Assert.IsNotType<BadRequestObjectResult>(result.Result);
        _conditions.Verify(s => s.RejectConditionAsync(EngagementId, ConditionId, Tenant, "client-1", It.IsAny<RejectApprovalDto>(), "client-1"), Times.Once);
    }

    [Fact]
    public async Task Reject_ReasonOver500Chars_HasExactMessage()
    {
        var result = await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = new string('r', 501) }, null);

        Assert.Equal("Rejection reason cannot exceed 500 characters.", Message(result.Result));
    }

    [Fact]
    public async Task Reject_BlankReason_HasExactMessage()
    {
        var result = await ClientOne().RejectCondition(EngagementId, ConditionId, new RejectApprovalDto { Reason = "  " }, null);

        Assert.Equal("Rejection reason is required.", Message(result.Result));
    }
}
