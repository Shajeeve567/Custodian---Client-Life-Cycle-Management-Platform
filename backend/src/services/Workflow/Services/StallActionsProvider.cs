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

    public async Task<StallActionsResult?> GetRawActionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        // Project only ClientId — we don't need the full entity, and reading the
        // Status/Stage columns would force enum conversion on data that may not
        // match the current enum shape (e.g. legacy seed rows).
        var clientId = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .Select(e => e.ClientId)
            .FirstOrDefaultAsync(ct);

        if (clientId is null) return null;

        var actions = await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId)
            .ToListAsync(ct);

        return new StallActionsResult(clientId, actions);
    }
}