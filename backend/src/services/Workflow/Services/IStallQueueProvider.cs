using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// Supplies active engagements and their actions for the staff stall queue,
/// scoped to a tenant. Batched (two queries, no N+1) and returns raw status
/// and stage strings rather than the enum entities — the DB may contain
/// legacy values that don't map to the current enums, and the queue must not
/// 500 on those rows.
/// </summary>
public interface IStallQueueProvider
{
    Task<IReadOnlyList<EngagementWithActions>> GetActiveEngagementsWithActionsAsync(string tenantId, CancellationToken ct = default);
}

public sealed record EngagementWithActions(
    Guid EngagementId,
    string TenantId,
    string ClientId,
    string StaffId,
    string StageRaw,
    IReadOnlyList<ClientAction> Actions
);