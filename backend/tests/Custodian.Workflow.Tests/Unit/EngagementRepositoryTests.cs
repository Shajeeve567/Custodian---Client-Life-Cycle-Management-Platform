using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// EngagementRepository and StallActionsProvider against the EF in-memory provider: every query is
/// scoped by engagement and tenant, and deleting an engagement removes everything that belongs to it.
/// </summary>
public class EngagementRepositoryTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";

    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase("engagement-repo-" + Guid.NewGuid().ToString("N")).Options);

    private EngagementRepository Repository => new(_db);

    private Engagement Seed(string tenant = Tenant, DateTime? createdAt = null, string clientId = "client-1")
    {
        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = tenant,
            ClientId = clientId,
            StaffId = "staff-1",
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        _db.Engagements.Add(engagement);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return engagement;
    }

    private void SeedChildren(Guid engagementId, string tenant)
    {
        _db.ClientActions.Add(new ClientAction { EngagementId = engagementId, TenantId = tenant, Title = "Upload passport" });
        _db.Requirements.Add(new Requirement { EngagementId = engagementId, TenantId = tenant });
        _db.EngagementConditions.Add(new EngagementCondition { EngagementId = engagementId, TenantId = tenant, Title = "Sign-off" });
        _db.StallRecords.Add(new StallRecord { EngagementId = engagementId, TenantId = tenant, ActionId = Guid.NewGuid() });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    // ---- Reads ----

    [Fact]
    public async Task GetByIdAsync_ReturnsTheEngagement_OnlyInItsTenant()
    {
        var engagement = Seed();

        Assert.Equal(engagement.EngagementId, (await Repository.GetByIdAsync(engagement.EngagementId, Tenant))!.EngagementId);
        Assert.Null(await Repository.GetByIdAsync(engagement.EngagementId, OtherTenant));
        Assert.Null(await Repository.GetByIdAsync(Guid.NewGuid(), Tenant));
    }

    [Fact]
    public async Task GetByIdAsync_DoesNotTrackTheResult()
    {
        var engagement = Seed();

        await Repository.GetByIdAsync(engagement.EngagementId, Tenant);

        Assert.Empty(_db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetAllByTenantAsync_ReturnsOnlyTheTenant_NewestFirst()
    {
        var older = Seed(createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Seed(createdAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Seed(OtherTenant);

        var all = (await Repository.GetAllByTenantAsync(Tenant)).ToList();

        Assert.Equal([newer.EngagementId, older.EngagementId], all.Select(e => e.EngagementId));
        Assert.Empty(_db.ChangeTracker.Entries());
    }

    // ---- Writes ----

    [Fact]
    public async Task CreateAsync_Persists_AndReturnsTheSameInstance()
    {
        var engagement = new Engagement { TenantId = Tenant, ClientId = "client-1", StaffId = "staff-1" };

        var created = await Repository.CreateAsync(engagement);

        Assert.Same(engagement, created);
        _db.ChangeTracker.Clear();
        Assert.NotNull(await _db.Engagements.FindAsync(engagement.EngagementId));
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesToADetachedEngagement()
    {
        var engagement = Seed();
        engagement.Status = EngagementStatus.Started;

        var updated = await Repository.UpdateAsync(engagement);

        Assert.Same(engagement, updated);
        _db.ChangeTracker.Clear();
        Assert.Equal(EngagementStatus.Started, (await _db.Engagements.FindAsync(engagement.EngagementId))!.Status);
    }

    [Fact]
    public async Task SaveChangesAsync_SavesPendingChanges()
    {
        _db.Engagements.Add(new Engagement { TenantId = Tenant });

        await Repository.SaveChangesAsync();

        Assert.Equal(1, await _db.Engagements.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheEngagementAndEverythingItOwns()
    {
        var engagement = Seed();
        SeedChildren(engagement.EngagementId, Tenant);

        Assert.True(await Repository.DeleteAsync(engagement.EngagementId, Tenant));

        Assert.Empty(_db.Engagements);
        Assert.Empty(_db.ClientActions);
        Assert.Empty(_db.Requirements);
        Assert.Empty(_db.EngagementConditions);
        Assert.Empty(_db.StallRecords);
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherEngagementsAndTenantsAlone()
    {
        var target = Seed();
        var sibling = Seed();
        SeedChildren(target.EngagementId, Tenant);
        SeedChildren(sibling.EngagementId, Tenant);
        // Same engagement id under another tenant: must not be touched.
        SeedChildren(target.EngagementId, OtherTenant);

        await Repository.DeleteAsync(target.EngagementId, Tenant);

        Assert.Equal(sibling.EngagementId, Assert.Single(_db.Engagements).EngagementId);
        Assert.Equal(2, await _db.ClientActions.CountAsync());
        Assert.Equal(2, await _db.Requirements.CountAsync());
        Assert.Equal(2, await _db.EngagementConditions.CountAsync());
        Assert.Equal(2, await _db.StallRecords.CountAsync());
        Assert.DoesNotContain(_db.ClientActions, a => a.EngagementId == target.EngagementId && a.TenantId == Tenant);
        Assert.DoesNotContain(_db.Requirements, r => r.EngagementId == target.EngagementId && r.TenantId == Tenant);
        Assert.DoesNotContain(_db.EngagementConditions, c => c.EngagementId == target.EngagementId && c.TenantId == Tenant);
        Assert.DoesNotContain(_db.StallRecords, s => s.EngagementId == target.EngagementId && s.TenantId == Tenant);
    }

    [Fact]
    public async Task DeleteAsync_WrongTenantOrUnknownId_ReturnsFalse_AndDeletesNothing()
    {
        var engagement = Seed();
        SeedChildren(engagement.EngagementId, Tenant);

        Assert.False(await Repository.DeleteAsync(engagement.EngagementId, OtherTenant));
        Assert.False(await Repository.DeleteAsync(Guid.NewGuid(), Tenant));

        Assert.Single(_db.Engagements);
        Assert.Single(_db.ClientActions);
    }

    // ---- StallActionsProvider ----

    [Fact]
    public async Task StallActions_ReturnsClientIdAndTheEngagementsActions_InTenant()
    {
        var engagement = Seed(clientId: "client-7");
        var other = Seed();
        SeedChildren(engagement.EngagementId, Tenant);
        SeedChildren(other.EngagementId, Tenant);
        SeedChildren(engagement.EngagementId, OtherTenant);

        var result = await new StallActionsProvider(_db).GetRawActionsAsync(engagement.EngagementId, Tenant);

        Assert.NotNull(result);
        Assert.Equal("client-7", result.ClientId);
        var action = Assert.Single(result.Actions);
        Assert.Equal(engagement.EngagementId, action.EngagementId);
        Assert.Equal(Tenant, action.TenantId);
    }

    [Fact]
    public async Task StallActions_UnknownEngagementOrWrongTenant_IsNull()
    {
        var engagement = Seed();

        Assert.Null(await new StallActionsProvider(_db).GetRawActionsAsync(engagement.EngagementId, OtherTenant));
        Assert.Null(await new StallActionsProvider(_db).GetRawActionsAsync(Guid.NewGuid(), Tenant));
    }
}
