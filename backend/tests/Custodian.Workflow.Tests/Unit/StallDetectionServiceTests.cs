using Custodian.Workflow.Configuration;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class StallDetectionServiceTests
{
    // Fixed reference time so tests never depend on wall-clock.
    private static readonly DateTime Now = new(2026, 09, 22, 12, 00, 00, DateTimeKind.Utc);

    private static StallDetectionService BuildService(int defaultHours = 72, Dictionary<int, int>? perStage = null)
    {
        var opts = new SlaOptions
        {
            DefaultOverdueHours = defaultHours,
            StageOverdueHours = perStage ?? new Dictionary<int, int>()
        };
        return new StallDetectionService(Options.Create(opts));
    }

    private static ClientAction MakeAction(
        DateTime? deadline = null,
        DateTime? createdAt = null,
        string status = ClientActionStatus.Pending,
        int stageNumber = 1,
        bool isInternalOnly = false) => new()
    {
        ActionId = Guid.NewGuid(),
        EngagementId = Guid.NewGuid(),
        TenantId = "t1",
        Title = "Test",
        Type = "CustomTask",
        Source = "Test",
        Status = status,
        StageNumber = stageNumber,
        DeadlineUtc = deadline,
        CreatedAt = createdAt ?? Now,
        IsInternalOnly = isInternalOnly
    };

    [Fact]
    public void Evaluate_NoDeadline_NoCreatedSla_UsesDefaultSla()
    {
        var svc = BuildService(defaultHours: 48);
        var action = MakeAction(createdAt: Now.AddHours(-50)); // 50h ago, SLA 48h -> overdue

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.True(result.IsStalled);
        Assert.Equal(2, result.HoursOverdue); // floor((50-48)) = 2
    }

    [Fact]
    public void Evaluate_BeforeDeadline_IsNotStalled()
    {
        var svc = BuildService();
        var action = MakeAction(deadline: Now.AddHours(1));

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.False(result.IsStalled);
        Assert.Null(result.HoursOverdue);
    }

    [Fact]
    public void Evaluate_ExactlyAtDeadline_IsNotStalled()
    {
        // Boundary: `>` not `>=` — at the exact second, deadline hasn't been missed yet.
        var svc = BuildService();
        var action = MakeAction(deadline: Now);

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public void Evaluate_OneSecondAfterDeadline_IsStalled()
    {
        var svc = BuildService();
        var action = MakeAction(deadline: Now.AddSeconds(-1));

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.True(result.IsStalled);
    }

    [Fact]
    public void Evaluate_CompletedAction_IsNeverStalled()
    {
        var svc = BuildService();
        var action = MakeAction(deadline: Now.AddDays(-10), status: ClientActionStatus.Completed);

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public void Evaluate_PerStageOverride_BeatsDefault()
    {
        var svc = BuildService(defaultHours: 999, perStage: new() { [2] = 24 });
        var action = MakeAction(stageNumber: 2, createdAt: Now.AddHours(-30)); // 30h vs 24h SLA → overdue

        var result = svc.Evaluate(action, action.EngagementId, Now);

        Assert.True(result.IsStalled);
        Assert.Equal(6, result.HoursOverdue); // 30 - 24
    }

    [Fact]
    public void EvaluateForEngagement_NoPendingActions_IsNotStalled()
    {
        var svc = BuildService();
        var completed = new[] { MakeAction(status: ClientActionStatus.Completed) };

        var result = svc.EvaluateForEngagement(Guid.NewGuid(), completed, Now);

        Assert.False(result.IsStalled);
        Assert.Null(result.ActionId);
    }

    [Fact]
    public void EvaluateForEngagement_PicksEarliestStagePendingAction()
    {
        var svc = BuildService();
        var early = MakeAction(stageNumber: 1, deadline: Now.AddDays(5));  // later deadline, earlier stage
        var late = MakeAction(stageNumber: 3, deadline: Now.AddHours(-1)); // earlier deadline, later stage

        var result = svc.EvaluateForEngagement(Guid.NewGuid(), new[] { early, late }, Now);

        // Current action = earliest unfinished stage, not the most overdue action.
        Assert.Equal(early.ActionId, result.ActionId);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void EvaluateForEngagement_IgnoresInternalOnlyActions()
    {
        var svc = BuildService();
        var internalOnly = MakeAction(
            deadline: Now.AddDays(-10),
            isInternalOnly: true);
        var visible = MakeAction(deadline: Now.AddHours(1));

        var result = svc.EvaluateForEngagement(Guid.NewGuid(), new[] { internalOnly, visible }, Now);

        // Stall is a client-facing concept — staff-only tasks must not flag a client stall.
        Assert.Equal(visible.ActionId, result.ActionId);
        Assert.False(result.IsStalled);
    }
}