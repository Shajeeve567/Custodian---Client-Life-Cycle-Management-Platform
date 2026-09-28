using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>Staff see stalls only on engagements they are responsible for; Owners see the whole workspace.</summary>
public class StallQueueControllerAccessTests
{
    private const string Tenant = "tenant-a";

    private static (StallQueueController Controller, Mock<IStallQueueService> Service) Create(string role, string userId)
    {
        var service = new Mock<IStallQueueService>();
        service.Setup(s => s.GetQueueAsync(Tenant, It.IsAny<StallQueueQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StallQueuePage(Array.Empty<StallQueueItemDto>(), 0, 1, 25));

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", Tenant),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, userId)
        ], "test"));

        var controller = new StallQueueController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
        return (controller, service);
    }

    [Fact]
    public async Task Staff_IsAlwaysLimitedToTheirOwnEngagements()
    {
        var (controller, service) = Create("Staff", "staff-a");

        await controller.GetQueue(tenantId: null, mine: false);

        service.Verify(s => s.GetQueueAsync(Tenant,
            It.Is<StallQueueQuery?>(q => q!.Mine && q.StaffId == "staff-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Owner_SeesTheWholeWorkspace_UnlessAskingForMine()
    {
        var (controller, service) = Create("Owner", "owner-1");

        await controller.GetQueue(tenantId: null, mine: false);

        service.Verify(s => s.GetQueueAsync(Tenant,
            It.Is<StallQueueQuery?>(q => !q!.Mine), It.IsAny<CancellationToken>()), Times.Once);
    }
}
