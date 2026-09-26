using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IStallQueueService
{
    /// <summary>
    /// Returns the tenant's stalled engagements ordered by urgency
    /// (most overdue first). Empty list when nothing is stalled.
    /// </summary>
    Task<IReadOnlyList<StallQueueItemDto>> GetQueueAsync(
        string tenantId, CancellationToken ct = default);
}