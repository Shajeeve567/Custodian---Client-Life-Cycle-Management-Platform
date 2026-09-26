using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class StallQueueServiceTests
{
    private readonly Mock<IStallQueueProvider> _mockProvider;
    private readonly IStallDetectionService _stallDetection;
    private readonly StallQueueService _service;

    public StallQueueServiceTests()
    {
        _mockProvider = new Mock<IStallQueueProvider>();
        _stallDetection = new StallDetectionService(
            Microsoft.Extensions.Options.Options.Create(
                new Custodian.Workflow.Configuration.SlaOptions
                {
                    DefaultOverdueHours = 72,
                    StageOverdueHours = new Dictionary<int, int> { [1] = 48 }
                }));
        _service = new StallQueueService(_mockProvider.Object, _stallDetection);
    }

    private static ClientAction OverdueClientAction(int hoursOverdue = 4, int stage = 1)
    {
        return new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            TenantId = "tenant-001",
            Title = "Upload ID",
            Type = "CustomTask",
            Source = "Test",
            Status = ClientActionStatus.Pending,
            StageNumber = stage,
            AssignedToRole = "Client",
            IsInternalOnly = false,
            DeadlineUtc = DateTime.UtcNow.AddHours(-hoursOverdue),
            CreatedAt = DateTime.UtcNow.AddHours(-48)
        };
    }

    private static EngagementWithActions Group(Guid engagementId, params ClientAction[] actions)
    {
        return new EngagementWithActions(
            engagementId,
            "tenant-001",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "Verification",
            actions);
    }

    [Fact]
    public async Task EmptyTenant_ReturnsEmptyQueue()
    {
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementWithActions>());

        var result = await _service.GetQueueAsync("tenant-001");

        Assert.Empty(result);
    }

    [Fact]
    public async Task EngagementWithOverdueClientAction_IsIncluded()
    {
        var engagementId = Guid.NewGuid();
        var action = OverdueClientAction(hoursOverdue: 5);
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(engagementId, action) });

        var result = await _service.GetQueueAsync("tenant-001");

        var item = Assert.Single(result);
        Assert.Equal(engagementId, item.EngagementId);
        Assert.Equal(action.ActionId, item.BlockerActionId);
        Assert.Equal("Upload ID", item.BlockerActionTitle);
        Assert.Equal(5, item.HoursOverdue);
        Assert.Equal("Verification", item.EngagementStage);
    }

    [Fact]
    public async Task EngagementWithNoOverdueAction_IsNotIncluded()
    {
        var action = OverdueClientAction();
        action.DeadlineUtc = DateTime.UtcNow.AddHours(24);

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        var result = await _service.GetQueueAsync("tenant-001");

        Assert.Empty(result);
    }

    [Fact]
    public async Task StaffAssignedOverdueAction_DoesNotAppearInQueue()
    {
        var action = OverdueClientAction();
        action.AssignedToRole = "Staff";

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        var result = await _service.GetQueueAsync("tenant-001");

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueueIsOrderedByHoursOverdueDescending()
    {
        var lessUrgent = OverdueClientAction(hoursOverdue: 2);
        var moreUrgent = OverdueClientAction(hoursOverdue: 20);
        var mostUrgent = OverdueClientAction(hoursOverdue: 50);

        var groups = new[]
        {
            Group(Guid.NewGuid(), lessUrgent),
            Group(Guid.NewGuid(), moreUrgent),
            Group(Guid.NewGuid(), mostUrgent),
        };

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(groups);

        var result = await _service.GetQueueAsync("tenant-001");

        Assert.Collection(result,
            first => Assert.Equal(50, first.HoursOverdue),
            second => Assert.Equal(20, second.HoursOverdue),
            third => Assert.Equal(2, third.HoursOverdue));
    }

    [Fact]
    public async Task ResolvedStall_LeavesTheQueue()
    {
        // Same engagement, two scenarios: action overdue then completed.
        var engagementId = Guid.NewGuid();
        var action = OverdueClientAction();

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(engagementId, action) });

        var beforeResolve = await _service.GetQueueAsync("tenant-001");
        Assert.Single(beforeResolve);

        action.Status = ClientActionStatus.Completed;

        var afterResolve = await _service.GetQueueAsync("tenant-001");
        Assert.Empty(afterResolve);
    }

    [Fact]
    public async Task NextAction_IsPopulatedWithActionTitle()
    {
        var action = OverdueClientAction();
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        var result = await _service.GetQueueAsync("tenant-001");

        var item = Assert.Single(result);
        Assert.Contains("Upload ID", item.NextAction);
    }
}