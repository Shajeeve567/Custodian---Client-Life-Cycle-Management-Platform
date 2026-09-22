using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// Supplies raw ClientAction rows for stall computation. Kept separate from
/// IClientActionService because the stall evaluator needs the entity (CreatedAt,
/// IsInternalOnly, Status) not the sanitized DTO.
/// Returns null when the engagement does not exist in the tenant.
/// </summary>
public interface IStallActionsProvider
{
    Task<IReadOnlyList<ClientAction>?> GetRawActionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}