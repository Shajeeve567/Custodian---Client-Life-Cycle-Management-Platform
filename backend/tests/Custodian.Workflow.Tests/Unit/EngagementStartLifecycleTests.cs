using Custodian.Workflow.Controllers;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Starting an engagement: tasks added to a draft stay inactive (no SLA clock) until it starts; starting
/// opens stage 1; a draft cannot advance stages; clients cannot act on tasks whose stage has not started.
/// </summary>
public class EngagementStartLifecycleTests
{
    private const string Tenant = "tenant-001";
    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly Guid _engagementId = Guid.NewGuid();

    private ClientActionService Actions => new(_db, _audit.Object);

    private async Task SeedEngagementAsync(EngagementStatus status, EngagementStage stage = EngagementStage.Onboarding)
    {
        _db.Engagements.Add(new Engagement { EngagementId = _engagementId, TenantId = Tenant, ClientId = "client-1", StaffId = "s1", Status = status, Stage = stage });
        await _db.SaveChangesAsync();
    }

    private Task<ClientActionResponseDto> AddTaskAsync(int stage) =>
        Actions.CreateActionAsync(_engagementId, Tenant, new CreateClientActionDto
        {
            Title = $"Stage {stage} task",
            Type = ClientActionType.CustomTask,
            StageNumber = stage,
            Source = "Test",
            AssignedToRole = "Client"
        });

    // Like a fresh request: nothing from the seeding is still tracked.
    private EngagementsController Controller()
    {
        _db.ChangeTracker.Clear();
        return new(new EngagementRepository(_db), _audit.Object, new Mock<IGateEvaluator>().Object, Actions);
    }

    [Fact]
    public async Task TaskAddedToADraft_IsNotActivated()
    {
        await SeedEngagementAsync(EngagementStatus.Draft);

        var created = await AddTaskAsync(stage: 1);

        Assert.Null((await _db.ClientActions.AsNoTracking().SingleAsync(a => a.ActionId == created.ActionId)).ActivatedAt);
    }

    [Fact]
    public async Task StartingTheEngagement_ActivatesStage1Tasks_AndLeavesLaterStagesInactive()
    {
        await SeedEngagementAsync(EngagementStatus.Draft);
        var stage1 = await AddTaskAsync(stage: 1);
        var stage2 = await AddTaskAsync(stage: 2);
        var before = DateTime.UtcNow;

        var result = await Controller().UpdateStatus(_engagementId, new UpdateEngagementStatusRequest { TenantId = Tenant, Status = "Started" });

        Assert.IsType<OkObjectResult>(result.Result);
        var s1 = await _db.ClientActions.AsNoTracking().SingleAsync(a => a.ActionId == stage1.ActionId);
        var s2 = await _db.ClientActions.AsNoTracking().SingleAsync(a => a.ActionId == stage2.ActionId);
        Assert.NotNull(s1.ActivatedAt);
        Assert.True(s1.ActivatedAt >= before); // the SLA clock starts at the start, not at creation
        Assert.Null(s2.ActivatedAt);
    }

    [Fact]
    public async Task TaskAddedToAStartedEngagementsCurrentStage_IsActivatedImmediately()
    {
        await SeedEngagementAsync(EngagementStatus.Started);

        var created = await AddTaskAsync(stage: 1);

        Assert.NotNull((await _db.ClientActions.AsNoTracking().SingleAsync(a => a.ActionId == created.ActionId)).ActivatedAt);
    }

    [Fact]
    public async Task ADraft_CannotAdvanceItsStage()
    {
        await SeedEngagementAsync(EngagementStatus.Draft);

        var result = await Controller().UpdateStage(_engagementId, new UpdateEngagementStageRequest { TenantId = Tenant, Stage = "DocumentCollection" });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(EngagementStatus.Draft, 1, true)]   // not started yet
    [InlineData(EngagementStatus.Started, 1, false)] // current stage: available
    [InlineData(EngagementStatus.Started, 3, true)]  // later stage: not yet
    public async Task ClientAvailability_FollowsEngagementStartAndStage(EngagementStatus status, int stage, bool expectBlocked)
    {
        await SeedEngagementAsync(status);
        var created = await AddTaskAsync(stage);

        var reason = await Actions.GetClientAvailabilityBlockReasonAsync(_engagementId, created.ActionId, Tenant);

        Assert.Equal(expectBlocked, reason != null);
        if (status == EngagementStatus.Started && expectBlocked)
        {
            Assert.Equal("This task opens in Stage 3.", reason);
        }
    }
}
