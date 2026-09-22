using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public sealed class StallActionsProvider : IStallActionsProvider
{
    private readonly WorkflowDbContext _dbContext;

    public StallActionsProvider(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ClientAction>?> GetRawActionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        // Confirm the engagement exists in this tenant first, so a valid tenant
        // with the wrong engagementId returns 404 rather than an empty stall status.
        var exists = await _dbContext.Engagements
            .AsNoTracking()
            .AnyAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId, ct);

        if (!exists) return null;

        return await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId)
            .ToListAsync(ct);
    }
}