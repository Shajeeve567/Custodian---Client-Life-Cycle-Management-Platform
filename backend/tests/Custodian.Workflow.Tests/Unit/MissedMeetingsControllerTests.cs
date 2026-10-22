using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Meetings;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

public class MissedMeetingsControllerTests
{
    private const string Tenant = "tenant-a";
    private readonly Mock<IMeetingService> _meetings = new();

    private MissedMeetingsController Controller(params Claim[] claims) =>
        new(_meetings.Object) { ControllerContext = ContextFor(claims) };

    [Fact]
    public async Task Missed_ReturnsTheTenantsMissedMeetings()
    {
        IReadOnlyList<MeetingResponse> missed = [new MeetingResponse { Status = "Missed" }];
        _meetings.Setup(s => s.ListMissedForTenantAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(missed);

        var result = await Controller(new Claim("tenant_id", " tenant-a ")).Missed(tenantId: null);

        Assert.Same(missed, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Missed_AcceptsTheCamelCaseTenantIdClaim()
    {
        await Controller(new Claim("tenantId", Tenant)).Missed(tenantId: null);

        _meetings.Verify(s => s.ListMissedForTenantAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missed_PrefersSnakeCaseTenantClaim_WhenBothArePresent()
    {
        await Controller(new Claim("tenant_id", Tenant), new Claim("tenantId", "other-tenant")).Missed(tenantId: null);

        _meetings.Verify(s => s.ListMissedForTenantAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missed_QueryTenantMatchingJwtIgnoringCase_IsAllowed()
    {
        var result = await Controller(new Claim("tenant_id", Tenant)).Missed(tenantId: " Tenant-A ");

        Assert.IsType<OkObjectResult>(result.Result);
        _meetings.Verify(s => s.ListMissedForTenantAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missed_QueryTenantDifferentFromJwt_IsForbidden()
    {
        var result = await Controller(new Claim("tenant_id", Tenant)).Missed(tenantId: "tenant-b");

        Assert.IsType<ForbidResult>(result.Result);
        _meetings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missed_NoJwtTenant_FallsBackToTrimmedQueryTenant()
    {
        await Controller().Missed(tenantId: "  tenant-a  ");

        _meetings.Verify(s => s.ListMissedForTenantAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Missed_NoTenantAnywhere_IsBadRequest(string? queryTenant)
    {
        var result = await Controller().Missed(queryTenant);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Tenant identification is required.", Message(result.Result));
        _meetings.VerifyNoOtherCalls();
    }
}
