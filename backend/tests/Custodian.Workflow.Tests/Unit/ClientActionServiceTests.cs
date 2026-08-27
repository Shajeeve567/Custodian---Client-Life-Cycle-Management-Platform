using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientActionServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    [Fact]
    public async Task GetActions_StaffCaller_ReturnsAllActionsIncludingInternalAndMetadata()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Public Client Action",
                Type = "DocumentUpload",
                Status = "Pending",
                Source = "Step1",
                IsInternalOnly = false,
                SourceMetadata = "{\"internalNote\":\"public step\"}"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Internal Staff Audit Check",
                Type = "VerificationCheck",
                Status = "Pending",
                Source = "SystemGate",
                IsInternalOnly = true,
                SourceMetadata = "{\"internalNote\":\"staff eyes only\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Call service with isClientView = false (Staff View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false);

        // Assert: Staff receives both items, including internal action and source metadata
        var list = result.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.IsInternalOnly);
        Assert.All(list, a => Assert.NotNull(a.SourceMetadata));
    }

    [Fact]
    public async Task GetActions_ClientCaller_StripsInternalActionsAndSanitizesSourceMetadata()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Upload ID Proof",
                Type = "DocumentUpload",
                Status = "Pending",
                Source = "Step1",
                IsInternalOnly = false,
                SourceMetadata = "{\"sensitiveData\":\"secret\"}"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Staff Background Verification",
                Type = "VerificationCheck",
                Status = "Pending",
                Source = "SystemGate",
                IsInternalOnly = true,
                SourceMetadata = "{\"internalNote\":\"secret\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Call service with isClientView = true (Client View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: true);

        // Assert: Client view strips internal actions and clears SourceMetadata
        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Upload ID Proof", list[0].Title);
        Assert.False(list[0].IsInternalOnly);
        Assert.Null(list[0].SourceMetadata);
    }

    [Fact]
    public async Task GetActions_CrossTenant_ReturnsEmptyList()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = "tenant-legitimate",
            Title = "Task",
            Status = "Pending",
            Source = "Step1"
        });
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Attempt to retrieve action with wrong tenant ID
        var result = await service.GetActionsByEngagementAsync(engagementId, "tenant-attacker", isClientView: false);

        // Assert: Expect empty list due to tenant isolation
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActions_StatusFilter_ReturnsFilteredActions()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Task 1",
                Status = "Pending",
                Source = "Step1"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Task 2",
                Status = "Completed",
                Source = "Step1"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Filter by status = "Completed"
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false, statusFilter: "Completed");

        // Assert
        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Completed", list[0].Status);
    }

    [Fact]
    public async Task CreateActionAsync_ValidRequest_PersistsAndReturnsDto()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var service = new ClientActionService(db);
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var createDto = new CreateClientActionDto
        {
            Title = "Verify Address",
            Description = "Upload utility bill",
            Type = "DocumentUpload",
            Source = "OnboardingStep2",
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = "{\"stepId\":\"2\"}"
        };

        // Act
        var created = await service.CreateActionAsync(engagementId, tenantId, createDto);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("Verify Address", created.Title);
        Assert.Equal("Pending", created.Status);
        Assert.Equal(engagementId, created.EngagementId);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == created.ActionId);
        Assert.NotNull(dbAction);
    }

    [Fact]
    public async Task CompleteActionAsync_ExistingAction_UpdatesStatusAndActor()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Sign Document",
            Status = "Pending",
            Source = "Step1"
        });
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        var completeDto = new CompleteClientActionDto
        {
            CompletedByActor = "staff-john-doe"
        };

        // Act
        var result = await service.CompleteActionAsync(engagementId, actionId, tenantId, completeDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("staff-john-doe", result.CompletedByActor);
        Assert.NotNull(result.CompletedAt);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal("Completed", dbAction!.Status);
    }
}
