using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public sealed class StallQueueProvider : IStallQueueProvider
{
    private readonly WorkflowDbContext _dbContext;

    public StallQueueProvider(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<EngagementWithActions>> GetActiveEngagementsWithActionsAsync(
        string tenantId, CancellationToken ct = default)
    {
        // Raw SQL for engagements: Status and Stage are mapped as enums with
        // .HasConversion<string>(), and any legacy value that doesn't parse to
        // an enum member causes EF to throw during materialization. This read
        // path is a view over existing data and must not 500 on one bad row.
        const string sql = @"
            SELECT engagement_id AS EngagementId,
                   tenant_id     AS TenantId,
                   client_id     AS ClientId,
                   staff_id      AS StaffId,
                   stage         AS StageRaw
            FROM engagements
            WHERE tenant_id = {0}
              AND status NOT IN ('Closed', 'Cancelled')";

        var engagements = await _dbContext.Database
            .SqlQueryRaw<RawEngagementRow>(sql, tenantId)
            .ToListAsync(ct);

        if (engagements.Count == 0)
        {
            return Array.Empty<EngagementWithActions>();
        }

        // Fetch all of the tenant's actions, then filter in memory.
        // Pomelo/EF Core 9 doesn't translate primitive-collection Contains
        // (ids.Contains(...)) without enabling primitive collection support,
        // so we avoid the construct entirely. Per-tenant action volume is
        // small — this is a queue view, not a bulk export.
        var activeEngagementIds = engagements
            .Select(e => e.EngagementId)
            .ToHashSet();

        var actions = await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(ct);

        var actionsByEngagement = actions
            .Where(a => activeEngagementIds.Contains(a.EngagementId))
            .GroupBy(a => a.EngagementId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ClientAction>)g.ToList());

        return engagements
            .Select(e =>
            {
                var engagementActions = actionsByEngagement.TryGetValue(e.EngagementId, out var list)
                    ? list
                    : Array.Empty<ClientAction>();

                return new EngagementWithActions(
                    e.EngagementId,
                    e.TenantId,
                    e.ClientId,
                    e.StaffId,
                    e.StageRaw ?? string.Empty,
                    engagementActions);
            })
            .ToList();
    }

    private sealed class RawEngagementRow
    {
        public Guid EngagementId { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string StaffId { get; set; } = string.Empty;
        public string? StageRaw { get; set; }
    }
}