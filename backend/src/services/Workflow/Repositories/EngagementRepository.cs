using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Repositories;

public class EngagementRepository : IEngagementRepository
{
    private readonly WorkflowDbContext _dbContext;

    public EngagementRepository(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Engagement?> GetByIdAsync(Guid id, string tenantId)
    {
        return await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == id && e.TenantId == tenantId);
    }

    public async Task<IEnumerable<Engagement>> GetAllByTenantAsync(string tenantId)
    {
        return await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();
    }

    public async Task<Engagement> CreateAsync(Engagement engagement)
    {
        _dbContext.Engagements.Add(engagement);
        await _dbContext.SaveChangesAsync();
        return engagement;
    }

    public async Task<Engagement> UpdateAsync(Engagement engagement)
    {
        _dbContext.Engagements.Update(engagement);
        await _dbContext.SaveChangesAsync();
        return engagement;
    }

    public async Task<bool> DeleteAsync(Guid id, string tenantId)
    {
        var engagement = await _dbContext.Engagements
            .FirstOrDefaultAsync(e => e.EngagementId == id && e.TenantId == tenantId);

        if (engagement == null)
        {
            return false;
        }

        // Remove everything that belongs to the engagement in the same save (one transaction), so a
        // deleted draft leaves no orphaned tasks, requirements, conditions or stall records behind.
        // The tables have no foreign keys to cascade from, hence the explicit removal.
        _dbContext.ClientActions.RemoveRange(
            await _dbContext.ClientActions.Where(a => a.EngagementId == id && a.TenantId == tenantId).ToListAsync());
        _dbContext.Requirements.RemoveRange(
            await _dbContext.Requirements.Where(r => r.EngagementId == id && r.TenantId == tenantId).ToListAsync());
        _dbContext.EngagementConditions.RemoveRange(
            await _dbContext.EngagementConditions.Where(c => c.EngagementId == id && c.TenantId == tenantId).ToListAsync());
        _dbContext.StallRecords.RemoveRange(
            await _dbContext.StallRecords.Where(s => s.EngagementId == id && s.TenantId == tenantId).ToListAsync());
        _dbContext.Engagements.Remove(engagement);

        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task SaveChangesAsync()
    {
        await _dbContext.SaveChangesAsync();
    }
}
