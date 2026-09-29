using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

/// <summary>
/// Database-only implementation of <see cref="IConditionReader"/>. Deliberately has no service
/// dependencies so it can be injected into GateEvaluator without a DI cycle.
/// </summary>
public class ConditionReader : IConditionReader
{
    private readonly WorkflowDbContext _dbContext;

    public ConditionReader(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<IReadOnlyList<EngagementCondition>> GetActiveConditionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default) =>
        QueryActiveAsync(_dbContext, engagementId, tenantId, ct);

    internal static async Task<IReadOnlyList<EngagementCondition>> QueryActiveAsync(
        WorkflowDbContext dbContext,
        Guid engagementId,
        string tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            return Array.Empty<EngagementCondition>();
        }

        return await dbContext.EngagementConditions
            .AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.TenantId == tenantId && c.IsActive)
            .OrderBy(c => c.RequiredBeforeStage)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }
}
