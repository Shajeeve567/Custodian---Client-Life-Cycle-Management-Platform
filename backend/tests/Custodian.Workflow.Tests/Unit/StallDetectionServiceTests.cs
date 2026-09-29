using Custodian.Workflow.Configuration;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class StallDetectionServiceTests
{
    private static readonly IStallDetectionService Svc = new StallDetectionService(
        Options.Create(new SlaOptions
        {
            DefaultOverdueHours = 72,
            StageOverdueHours = new Dictionary<int, int> { [1] = 48, [2] = 72 }
        }));

    private static ClientAction BuildAction(
        string assignedToRole = "Client",
        bool isInternalOnly = false,
        string status = ClientActionStatus.Pending,
        DateTime? deadlineUtc = null,
        int stageNumber = 1)
    {
        return new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            TenantId = "tenant-001",
            Title = "Test Action",
            Type = "CustomTask",
            Source = "Test",
            Status = status,
            StageNumber = stageNumber,
            AssignedToRole = assignedToRole,
            IsInternalOnly = isInternalOnly,
            DeadlineUtc = deadlineUtc ?? DateTime.UtcNow.AddHours(-4),
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            // CSTD-21: the action's stage has started (only activated actions have an SLA)
            ActivatedAt = DateTime.UtcNow.AddHours(-48)
        };
    }

    [Fact]
    public void OverdueClientAction_IsDetected()
    {
        var action = BuildAction(assignedToRole: "Client");
        var result = Svc.EvaluateForEngagement(Guid.NewGuid(), new[] { action }, DateTime.UtcNow);
        Assert.True(result.IsStalled);
        Assert.Equal(action.ActionId, result.ActionId);
    }

    [Fact]
    public void OverdueStaffAction_IsNotDetected()
    {
        var action = BuildAction(assignedToRole: "Staff");
        var result = Svc.EvaluateForEngagement(Guid.NewGuid(), new[] { action }, DateTime.UtcNow);
        Assert.False(result.IsStalled);
        Assert.Null(result.ActionId);
    }

    [Fact]
    public void OverdueOwnerAction_IsNotDetected()
    {
        var action = BuildAction(assignedToRole: "Owner");
        var result = Svc.EvaluateForEngagement(Guid.NewGuid(), new[] { action }, DateTime.UtcNow);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void OverdueInternalOnlyClientAction_IsNotDetected()
    {
        var action = BuildAction(assignedToRole: "Client", isInternalOnly: true);
        var result = Svc.EvaluateForEngagement(Guid.NewGuid(), new[] { action }, DateTime.UtcNow);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void CompletedClientAction_IsNotDetected()
    {
        var action = BuildAction(assignedToRole: "Client", status: ClientActionStatus.Completed);
        var result = Svc.EvaluateForEngagement(Guid.NewGuid(), new[] { action }, DateTime.UtcNow);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void ClientAndStaffActionsBothOverdue_SelectsClientAction()
    {
        var staffAction = BuildAction(assignedToRole: "Staff", stageNumber: 1);
        var clientAction = BuildAction(assignedToRole: "Client", stageNumber: 2);

        var result = Svc.EvaluateForEngagement(
            Guid.NewGuid(), new[] { staffAction, clientAction }, DateTime.UtcNow);

        Assert.True(result.IsStalled);
        Assert.Equal(clientAction.ActionId, result.ActionId);
    }

    // =========================================================================
    // CSTD-21 integration: SLA applies only once the action's stage has started
    // =========================================================================

    [Fact]
    public void FutureStageActionWithoutActivatedAt_NeverStalls_AndShowsOnlyExplicitDeadline()
    {
        var future = BuildAction(assignedToRole: "Client", stageNumber: 4, deadlineUtc: DateTime.UtcNow.AddHours(-10));
        future.ActivatedAt = null;

        var single = Svc.Evaluate(future, future.EngagementId, DateTime.UtcNow);
        var forEngagement = Svc.EvaluateForEngagement(future.EngagementId, new[] { future }, DateTime.UtcNow);

        Assert.False(single.IsStalled);
        Assert.Equal(future.DeadlineUtc, single.DeadlineUtc);
        Assert.False(forEngagement.IsStalled);
    }

    [Fact]
    public void SlaClock_StartsAtActivatedAt_NotCreatedAt()
    {
        // Created 10 days ago for a later stage; that stage started 1 hour ago. Stage 1 SLA = 48h.
        var now = DateTime.UtcNow;
        var action = BuildAction(assignedToRole: "Client", stageNumber: 1);
        action.DeadlineUtc = null;
        action.CreatedAt = now.AddDays(-10);
        action.ActivatedAt = now.AddHours(-1);

        var result = Svc.Evaluate(action, action.EngagementId, now);

        Assert.False(result.IsStalled);
        Assert.Equal(action.ActivatedAt.Value.AddHours(48), result.DeadlineUtc);
        Assert.Equal(action.ActivatedAt.Value.AddHours(48), Svc.ResolveEffectiveDeadline(action));
    }

    [Fact]
    public void CancelledAction_IsNeverStalled()
    {
        var action = BuildAction(assignedToRole: "Client", status: ClientActionStatus.Cancelled);

        Assert.False(Svc.Evaluate(action, action.EngagementId, DateTime.UtcNow).IsStalled);
        Assert.False(Svc.EvaluateForEngagement(action.EngagementId, new[] { action }, DateTime.UtcNow).IsStalled);
    }

    [Fact]
    public void AtTheExactDeadline_IsNotYetStalled()
    {
        var now = DateTime.UtcNow;
        var action = BuildAction(assignedToRole: "Client", deadlineUtc: now);

        Assert.False(Svc.Evaluate(action, action.EngagementId, now).IsStalled);
        Assert.True(Svc.Evaluate(action, action.EngagementId, now.AddSeconds(1)).IsStalled);
    }
}
