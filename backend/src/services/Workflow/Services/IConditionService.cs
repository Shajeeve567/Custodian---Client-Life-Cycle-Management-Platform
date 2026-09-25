using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

public interface IConditionService
{
    /// <summary>
    /// Returns active conditions for an engagement, tenant-scoped, ordered by
    /// RequiredBeforeStage and CreatedAt. Used by GateEvaluator and next-action orchestration.
    /// </summary>
    Task<IReadOnlyList<EngagementCondition>> GetActiveConditionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Staff view: returns all conditions (or filtered by includeInactive).
    /// </summary>
    Task<IEnumerable<ConditionResponseDto>> GetConditionsStaffAsync(Guid engagementId, string tenantId, bool includeInactive = true);

    /// <summary>
    /// Client view: returns only active conditions in a client-safe DTO. Enforces engagement ownership.
    /// </summary>
    Task<IEnumerable<ClientSafeConditionDto>> GetConditionsClientAsync(Guid engagementId, string tenantId, string callerClientId);

    /// <summary>
    /// Staff gets a specific condition by ID.
    /// </summary>
    Task<ConditionResponseDto?> GetConditionByIdStaffAsync(Guid engagementId, Guid conditionId, string tenantId);

    /// <summary>
    /// Client gets a specific active condition by ID in client-safe format.
    /// </summary>
    Task<ClientSafeConditionDto?> GetConditionByIdClientAsync(Guid engagementId, Guid conditionId, string tenantId, string callerClientId);

    /// <summary>
    /// Attaches an Approval or Payment condition, enforces duplicate active checks,
    /// creates linked ClientAction, and publishes ConditionAttached audit event.
    /// </summary>
    Task<ConditionResponseDto> AttachConditionAsync(Guid engagementId, string tenantId, AttachConditionDto dto, string actor);

    /// <summary>
    /// Updates configurable fields while the condition is active and pending.
    /// </summary>
    Task<ConditionResponseDto> UpdateConditionAsync(Guid engagementId, Guid conditionId, string tenantId, UpdateConditionDto dto, string actor);

    /// <summary>
    /// Deactivates a condition (idempotent), cancels linked ClientActions, and publishes ConditionDeactivated audit event.
    /// </summary>
    Task<ConditionResponseDto> DeactivateConditionAsync(Guid engagementId, Guid conditionId, string tenantId, DeactivateConditionDto dto, string actor);

    /// <summary>
    /// Framework status transition helper for CSTD-25/26. Guarded: only active conditions,
    /// Pending -> Satisfied | Rejected, Rejected -> Pending.
    /// </summary>
    Task SetConditionStatusAsync(Guid conditionId, string tenantId, string newStatus, string actor, string? reason = null);
}
