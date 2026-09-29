using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Custodian.Workflow.Services.Stall;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.NextAction;
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
        _service = NewService();
    }

    // The real recorder on an in-memory database: stall records persist across calls in one test.
    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IAuditPublisher> _auditPublisher = new();

    private StallQueueService NewService(INextActionService? engine = null) =>
        new(_mockProvider.Object,
            new StallRecorder(_db, _stallDetection, _auditPublisher.Object, TimeProvider.System, NullLogger<StallRecorder>.Instance),
            engine);

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
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            ActivatedAt = DateTime.UtcNow.AddHours(-48) // CSTD-21: stage has started
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

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

        Assert.Empty(result);
    }

    [Fact]
    public async Task EngagementWithOverdueClientAction_IsIncluded()
    {
        var engagementId = Guid.NewGuid();
        var action = OverdueClientAction(hoursOverdue: 5);
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(engagementId, action) });

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

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

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

        Assert.Empty(result);
    }

    [Fact]
    public async Task StaffAssignedOverdueAction_DoesNotAppearInQueue()
    {
        var action = OverdueClientAction();
        action.AssignedToRole = "Staff";

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

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

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

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

        var beforeResolve = (await _service.GetQueueAsync("tenant-001")).Items;
        Assert.Single(beforeResolve);

        action.Status = ClientActionStatus.Completed;

        var afterResolve = (await _service.GetQueueAsync("tenant-001")).Items;
        Assert.Empty(afterResolve);
    }

    [Fact]
    public async Task NextAction_IsPopulatedWithActionTitle()
    {
        var action = OverdueClientAction();
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        var result = (await _service.GetQueueAsync("tenant-001")).Items;

        var item = Assert.Single(result);
        Assert.Contains("Upload ID", item.NextAction);
    }

    // =========================================================================
    // CSTD-19 integration: "next action" column comes from the next-action engine
    // =========================================================================

    [Fact]
    public async Task NextAction_ComesFromNextActionEngine_WhenAvailable()
    {
        var engagementId = Guid.NewGuid();
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(engagementId, OverdueClientAction(hoursOverdue: 5)) });

        var engine = new Mock<INextActionService>();
        engine.Setup(e => e.GetNextActionAsync(engagementId, "tenant-001", NextActionView.Staff, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NextActionResult
            {
                EngagementId = engagementId,
                PrimaryAction = new NextActionItem
                {
                    Kind = NextActionKind.DocumentUpload,
                    ResponsibleParty = ResponsibleParty.Client,
                    Title = "Upload ID",
                    Reason = "Action is overdue.",
                    PriorityRank = 1
                }
            });
        var service = NewService(engine.Object);

        var item = Assert.Single((await service.GetQueueAsync("tenant-001")).Items);

        Assert.Equal("Upload ID", item.NextAction);
        Assert.Equal(ResponsibleParty.Client, item.NextActionResponsibleParty);
    }

    [Fact]
    public async Task NextAction_FallsBackToAdvisoryText_WhenEngineHasNoPrimary()
    {
        var engagementId = Guid.NewGuid();
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(engagementId, OverdueClientAction()) });

        var engine = new Mock<INextActionService>();
        engine.Setup(e => e.GetNextActionAsync(engagementId, "tenant-001", NextActionView.Staff, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NextActionResult?)null);
        var service = NewService(engine.Object);

        var item = Assert.Single((await service.GetQueueAsync("tenant-001")).Items);

        Assert.Equal("Contact client regarding 'Upload ID'", item.NextAction);
        Assert.Null(item.NextActionResponsibleParty);
    }

    [Fact]
    public async Task ActionWhoseStageHasNotStarted_DoesNotAppearInQueue()
    {
        var action = OverdueClientAction(stage: 4);
        action.ActivatedAt = null; // CSTD-21: stage 4 has not started yet

        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Group(Guid.NewGuid(), action) });

        Assert.Empty((await _service.GetQueueAsync("tenant-001")).Items);
    }

    // =========================================================================
    // CSTD-34 (M2): urgency score, filters, paging
    // =========================================================================

    private static EngagementWithActions GroupFor(Guid engagementId, string staffId, string stage, ClientAction action) =>
        new(engagementId, "tenant-001", Guid.NewGuid().ToString(), staffId, stage, new[] { action });

    private void Queue(params EngagementWithActions[] groups) =>
        _mockProvider.Setup(p => p.GetActiveEngagementsWithActionsAsync("tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(groups);

    [Fact]
    public async Task StageGatingBlocker_OutranksAPlainTaskThatIsSomewhatMoreOverdue()
    {
        var plain = OverdueClientAction(hoursOverdue: 30);
        var requirement = OverdueClientAction(hoursOverdue: 20);
        requirement.Type = ClientActionType.Requirement; // gates the next stage: weight 2.0 -> score 40 vs 30
        var plainEngagement = Guid.NewGuid();
        var requirementEngagement = Guid.NewGuid();
        Queue(Group(plainEngagement, plain), Group(requirementEngagement, requirement));

        var items = (await _service.GetQueueAsync("tenant-001")).Items;

        Assert.Equal(new[] { requirementEngagement, plainEngagement }, items.Select(i => i.EngagementId));
        Assert.Equal(40, items[0].UrgencyScore);
        Assert.Equal(30, items[1].UrgencyScore);
    }

    [Fact]
    public async Task Filters_Mine_Stage_AndMinOverdueHours()
    {
        var mineInReview = Guid.NewGuid();
        Queue(
            GroupFor(mineInReview, "staff-me", "Review", OverdueClientAction(hoursOverdue: 10)),
            GroupFor(Guid.NewGuid(), "staff-other", "Review", OverdueClientAction(hoursOverdue: 10)),
            GroupFor(Guid.NewGuid(), "staff-me", "Onboarding", OverdueClientAction(hoursOverdue: 10)),
            GroupFor(Guid.NewGuid(), "staff-me", "Review", OverdueClientAction(hoursOverdue: 2)));

        var result = await _service.GetQueueAsync("tenant-001",
            new StallQueueQuery(Mine: true, StaffId: "staff-me", Stage: "review", MinOverdueHours: 5));

        Assert.Equal(mineInReview, Assert.Single(result.Items).EngagementId);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Paging_ReturnsTheRequestedSlice_WithTheTotalBeforePaging_AndEvaluatesTheEngineOnlyForThatSlice()
    {
        var groups = Enumerable.Range(1, 5)
            .Select(i => Group(Guid.NewGuid(), OverdueClientAction(hoursOverdue: i * 10)))
            .ToArray();
        Queue(groups);
        var engine = new Mock<INextActionService>();
        var service = NewService(engine.Object);

        var page2 = await service.GetQueueAsync("tenant-001", new StallQueueQuery(Page: 2, PageSize: 2));

        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        // Most overdue first: 50, 40 on page 1; 30, 20 on page 2.
        Assert.Equal(new[] { 30, 20 }, page2.Items.Select(i => i.HoursOverdue));
        engine.Verify(e => e.GetNextActionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<NextActionView>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PageSize_IsCappedAt100()
    {
        Queue(Group(Guid.NewGuid(), OverdueClientAction()));

        var result = await _service.GetQueueAsync("tenant-001", new StallQueueQuery(PageSize: 5000));

        Assert.Equal(StallQueueQuery.MaxPageSize, result.PageSize);
    }

    [Fact]
    public async Task ReadingTheQueue_RecordsTheStallOnce_AndReportsWhenItStarted()
    {
        var engagementId = Guid.NewGuid();
        Queue(Group(engagementId, OverdueClientAction(hoursOverdue: 5)));

        var first = Assert.Single((await _service.GetQueueAsync("tenant-001")).Items);
        var second = Assert.Single((await _service.GetQueueAsync("tenant-001")).Items);

        Assert.Equal(first.StalledSinceUtc, second.StalledSinceUtc);
        Assert.Equal(1, second.OpenStallCount);
        _auditPublisher.Verify(a => a.PublishEventAsync(engagementId, "tenant-001", It.IsAny<string>(), "action.overdue", It.IsAny<object>()), Times.Once);
    }
}
