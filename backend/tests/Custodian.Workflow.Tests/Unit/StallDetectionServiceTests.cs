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
            CreatedAt = DateTime.UtcNow.AddHours(-48)
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
}