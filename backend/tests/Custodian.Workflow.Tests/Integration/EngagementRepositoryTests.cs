using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// Integration tests for EngagementRepository using EF Core In-Memory Database.
/// We test real database interaction logic without needing a running MySQL instance!
/// </summary>
public class EngagementRepositoryTests
{
    private static WorkflowDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    [Fact]
    public async Task CreateAsync_ValidEngagement_ShouldPersistInDatabase()
    {
        // Arrange: Setup unique database for this test run
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        // Act: Save engagement using repository
        var result = await repository.CreateAsync(engagement);

        // Assert: Verify it exists in DB
        var savedInDb = await context.Engagements.FindAsync(engagement.EngagementId);
        Assert.NotNull(savedInDb);
        Assert.Equal("tenant-001", savedInDb.TenantId);
        Assert.Equal("client-001", savedInDb.ClientId);
    }

    [Fact]
    public async Task GetByIdAsync_SameTenant_ShouldReturnEngagement()
    {
        // Arrange
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Draft
        };
        context.Engagements.Add(engagement);
        await context.SaveChangesAsync();

        // Act: Query by matching tenantId
        var result = await repository.GetByIdAsync(engagementId, "tenant-001");

        // Assert: Should successfully retrieve engagement
        Assert.NotNull(result);
        Assert.Equal(engagementId, result.EngagementId);
    }

    [Fact]
    public async Task GetByIdAsync_DifferentTenant_ShouldReturnNull_RejectsCrossTenantAccess()
    {
        // Arrange: Store engagement under tenant-001
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        var engagementId = Guid.NewGuid();
        context.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Draft
        });
        await context.SaveChangesAsync();

        // Act: Attacker from tenant-999 tries to read tenant-001's engagement
        var result = await repository.GetByIdAsync(engagementId, "tenant-999");

        // Assert: MUST return null to prevent cross-tenant data leakage!
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllByTenantAsync_ShouldOnlyReturnRecordsForSpecificTenant()
    {
        // Arrange: Add 2 records for tenant-A and 1 record for tenant-B
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        context.Engagements.AddRange(
            new Engagement { EngagementId = Guid.NewGuid(), TenantId = "tenant-A", ClientId = "c1", StaffId = "s1" },
            new Engagement { EngagementId = Guid.NewGuid(), TenantId = "tenant-A", ClientId = "c2", StaffId = "s1" },
            new Engagement { EngagementId = Guid.NewGuid(), TenantId = "tenant-B", ClientId = "c3", StaffId = "s2" }
        );
        await context.SaveChangesAsync();

        // Act: Fetch all for tenant-A
        var tenantAEngagements = (await repository.GetAllByTenantAsync("tenant-A")).ToList();

        // Assert: Should return exactly 2 records, ignoring tenant-B's record
        Assert.Equal(2, tenantAEngagements.Count);
        Assert.All(tenantAEngagements, e => Assert.Equal("tenant-A", e.TenantId));
    }

    [Fact]
    public async Task UpdateAsync_ValidChanges_ShouldUpdateDatabase()
    {
        // Arrange
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        var engagementId = Guid.NewGuid();
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "c1",
            StaffId = "s1",
            Status = EngagementStatus.Draft
        };
        context.Engagements.Add(engagement);
        await context.SaveChangesAsync();

        // Act: Update status to Started
        engagement.Status = EngagementStatus.Started;
        await repository.UpdateAsync(engagement);

        // Assert: Database state should be updated
        var updatedInDb = await context.Engagements.FindAsync(engagementId);
        Assert.NotNull(updatedInDb);
        Assert.Equal(EngagementStatus.Started, updatedInDb.Status);
    }

    [Fact]
    public async Task DeleteAsync_DraftEngagement_ShouldRemoveFromDatabase()
    {
        // Arrange
        using var context = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new EngagementRepository(context);

        var engagementId = Guid.NewGuid();
        context.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-001",
            ClientId = "c1",
            StaffId = "s1",
            Status = EngagementStatus.Draft
        });
        await context.SaveChangesAsync();

        // Act: Delete draft engagement
        await repository.DeleteAsync(engagementId, "tenant-001");

        // Assert: Record should no longer exist in DB
        var existsInDb = await context.Engagements.FindAsync(engagementId);
        Assert.Null(existsInDb);
    }
}
