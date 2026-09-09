using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientPortalServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    [Fact]
    public async Task GetDashboard_CalculatesProgressBasedOnTotalCompletedTasks()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started
        });

        // Add 4 client tasks (2 completed, 2 pending) + 1 internal task (which shouldn't count in client progress)
        db.ClientActions.AddRange(
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 1", Status = ClientActionStatus.Completed, StageNumber = 1, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 2", Status = ClientActionStatus.Completed, StageNumber = 1, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 3", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 4", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Staff Audit", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = true }
        );
        await db.SaveChangesAsync();

        var service = new ClientPortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: 2 completed out of 4 client tasks = 50%
        Assert.NotNull(dashboard);
        Assert.Equal(4, dashboard.TotalTasksCount);
        Assert.Equal(2, dashboard.CompletedTasksCount);
        Assert.Equal(50, dashboard.ProgressPercentage);
    }

    [Fact]
    public async Task GetDashboard_SelectsPrimaryNextAction_BasedOnActiveOnboardingStage()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started
        });

        // Stage 1 completed
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake Form",
            Status = ClientActionStatus.Completed,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });

        // Stage 2 has two pending tasks (one with earlier deadline)
        var earlyDeadline = DateTime.UtcNow.AddDays(1);
        var laterDeadline = DateTime.UtcNow.AddDays(5);

        db.ClientActions.AddRange(
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Corporate Charter Upload",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = laterDeadline,
                IsInternalOnly = false,
                AssignedToRole = "Client"
            },
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Proof of Address Upload",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = earlyDeadline,
                IsInternalOnly = false,
                AssignedToRole = "Client"
            },
            // Stage 3 also has a pending task, but Stage 2 must take priority!
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Retainer Escrow Deposit",
                Status = ClientActionStatus.Pending,
                StageNumber = 3,
                DeadlineUtc = DateTime.UtcNow.AddHours(2), // earlier deadline, but in a later stage!
                IsInternalOnly = false,
                AssignedToRole = "Client"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientPortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: Primary action must be from Stage 2 (the current stage), with earliest deadline ("Proof of Address Upload")
        Assert.NotNull(dashboard);
        Assert.Equal(2, dashboard.CurrentStageNumber);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal("Proof of Address Upload", dashboard.PrimaryNextAction.Title);
        Assert.Equal(2, dashboard.PrimaryNextAction.StageNumber);
        Assert.Equal("ActionRequired", dashboard.ConditionStatus);
    }

    [Fact]
    public async Task GetDashboard_WhenActionUploaded_SetsConditionStatusToUnderReview()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started
        });

        // Action was uploaded by client, awaiting staff review
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Passport Identity Copy",
            Status = ClientActionStatus.Uploaded,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = new ClientPortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Null(dashboard.PrimaryNextAction); // No action required by client right now
        Assert.Equal("UnderReview", dashboard.ConditionStatus);
    }

    [Fact]
    public async Task GetDashboard_DifferentClient_EnforcesOwnershipAndReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var legitimateClient = "client-owner-123";
        var attackerClient = "client-attacker-999";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = legitimateClient,
            StaffId = "staff-1",
            Status = EngagementStatus.Started
        });
        await db.SaveChangesAsync();

        var service = new ClientPortalService(db);

        // Act: Attempt to access engagement owned by client-owner-123 using client-attacker-999
        var result = await service.GetDashboardForEngagementAsync(engagementId, tenantId, attackerClient);

        // Assert: Must be rejected (returns null)
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveDashboardForClient_ResolvesActiveEngagementAutomatically()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var tenantId = "tenant-test";
        var clientId = "client-active-user";

        // Older closed engagement
        db.Engagements.Add(new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Closed,
            CreatedAt = DateTime.UtcNow.AddMonths(-2)
        });

        // Active ongoing engagement
        var activeEngagementId = Guid.NewGuid();
        db.Engagements.Add(new Engagement
        {
            EngagementId = activeEngagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        });
        await db.SaveChangesAsync();

        var service = new ClientPortalService(db);

        // Act: Client doesn't pass any engagement GUID
        var dashboard = await service.GetActiveDashboardForClientAsync(tenantId, clientId);

        // Assert: Automatically resolves the active "Started" engagement
        Assert.NotNull(dashboard);
        Assert.Equal(activeEngagementId, dashboard.EngagementId);
    }
}
