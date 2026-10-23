using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>StallQueueController (CSTD-34): tenant resolution, the "mine" filter, paging and the total-count header.</summary>
public class StallQueueControllerTests
{
    private const string Tenant = "tenant-a";
    private readonly Mock<IStallQueueService> _service = new();
    private StallQueueQuery? _query;
    private string? _tenant;

    public StallQueueControllerTests()
    {
        _service.Setup(s => s.GetQueueAsync(It.IsAny<string>(), It.IsAny<StallQueueQuery?>(), It.IsAny<CancellationToken>()))
            .Callback((string tenant, StallQueueQuery? query, CancellationToken _) => { _tenant = tenant; _query = query; })
            .ReturnsAsync(new StallQueuePage([new StallQueueItemDto()], 42, 1, 25));
    }

    private StallQueueController Controller(params Claim[] claims) => new(_service.Object) { ControllerContext = ContextFor(claims) };

    private StallQueueController Owner(params Claim[] extra) =>
        Controller([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), .. extra]);

    private static StallQueueController WithHeader(StallQueueController controller, string value)
    {
        controller.HttpContext.Request.Headers["X-Tenant-ID"] = value;
        return controller;
    }

    [Fact]
    public async Task GetQueue_ReturnsItems_AndTotalCountHeader()
    {
        var c = Owner(new Claim(ClaimTypes.NameIdentifier, "owner-1"));

        var result = await c.GetQueue(tenantId: null);

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<StallQueueItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal("42", c.Response.Headers[StallQueueController.TotalCountHeader].ToString());
        Assert.Equal("X-Total-Count", StallQueueController.TotalCountHeader);
    }

    [Fact]
    public async Task GetQueue_PassesFiltersAndPagingThrough()
    {
        await Owner(new Claim(ClaimTypes.NameIdentifier, "owner-1"))
            .GetQueue(null, mine: true, stage: "Verification", minOverdueHours: 48, page: 3, pageSize: 10);

        Assert.Equal(Tenant, _tenant);
        Assert.NotNull(_query);
        Assert.True(_query.Mine);
        Assert.Equal("owner-1", _query.StaffId);
        Assert.Equal("Verification", _query.Stage);
        Assert.Equal(48, _query.MinOverdueHours);
        Assert.Equal(3, _query.Page);
        Assert.Equal(10, _query.PageSize);
    }

    [Fact]
    public async Task GetQueue_OwnerMine_UsesSubClaim_WhenNoNameIdentifier()
    {
        await Owner(new Claim("sub", "owner-sub")).GetQueue(null, mine: true);

        Assert.Equal("owner-sub", _query!.StaffId);
    }

    [Fact]
    public async Task GetQueue_OwnerMine_PrefersNameIdentifierOverSub()
    {
        await Owner(new Claim(ClaimTypes.NameIdentifier, "owner-1"), new Claim("sub", "owner-sub")).GetQueue(null, mine: true);

        Assert.Equal("owner-1", _query!.StaffId);
    }

    [Fact]
    public async Task GetQueue_OwnerMine_WithoutIdentity_IsBadRequest()
    {
        var result = await Owner().GetQueue(null, mine: true);

        Assert.Equal("mine=true needs a signed-in staff member.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetQueue_OwnerWithoutIdentity_CanStillSeeTheWholeWorkspace()
    {
        var result = await Owner().GetQueue(null, mine: false);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(_query!.Mine);
    }

    [Fact]
    public async Task GetQueue_Tenant_JwtMismatchWithQueryOrHeader_IsForbidden()
    {
        Assert.IsType<ForbidResult>((await Owner().GetQueue("tenant-b")).Result);
        Assert.IsType<ForbidResult>((await WithHeader(Owner(), "tenant-b").GetQueue(null)).Result);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetQueue_Tenant_MatchesIgnoringCase_AndBlankHeaderIsIgnored()
    {
        Assert.IsType<OkObjectResult>((await WithHeader(Owner(), " TENANT-A ").GetQueue(" Tenant-A ")).Result);
        Assert.Equal(Tenant, _tenant);

        _tenant = null;
        Assert.IsType<OkObjectResult>((await WithHeader(Owner(), " ").GetQueue(null)).Result);
        Assert.Equal(Tenant, _tenant);
    }

    [Fact]
    public async Task GetQueue_Tenant_JwtIsTrimmed_CamelCaseClaimAccepted_SnakeCaseWins()
    {
        await Controller(new Claim("tenantId", " tenant-a "), new Claim(ClaimTypes.Role, "Owner")).GetQueue(null);
        Assert.Equal(Tenant, _tenant);

        _tenant = null;
        await Controller(new Claim("tenant_id", Tenant), new Claim("tenantId", "other"), new Claim(ClaimTypes.Role, "Owner")).GetQueue(null);
        Assert.Equal(Tenant, _tenant);
    }

    [Fact]
    public async Task GetQueue_Tenant_NoJwt_HeaderBeatsQuery_BlankHeaderFallsBackToQuery()
    {
        await WithHeader(Controller(new Claim(ClaimTypes.Role, "Owner")), " tenant-a ").GetQueue("ignored");
        Assert.Equal(Tenant, _tenant);

        _tenant = null;
        var result = await WithHeader(Controller(new Claim(ClaimTypes.Role, "Owner")), "  ").GetQueue(" tenant-a ");
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Tenant, _tenant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task GetQueue_Tenant_Nowhere_IsBadRequest(string? query)
    {
        var result = await Controller(new Claim(ClaimTypes.Role, "Owner")).GetQueue(query);

        Assert.Equal("Tenant identification is required.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
        _service.VerifyNoOtherCalls();
    }
}
