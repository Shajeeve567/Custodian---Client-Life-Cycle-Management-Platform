using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IClientActionService
{
    Task<IEnumerable<ClientActionResponseDto>> GetActionsByEngagementAsync(Guid engagementId, string tenantId, bool isClientView, string? statusFilter = null);
    Task<ClientActionResponseDto> CreateActionAsync(Guid engagementId, string tenantId, CreateClientActionDto dto);
    Task<ClientActionResponseDto?> CompleteActionAsync(Guid engagementId, Guid actionId, string tenantId, CompleteClientActionDto dto);
    Task<ClientActionResponseDto?> UploadEvidenceAsync(Guid engagementId, Guid actionId, string tenantId, UploadActionEvidenceDto dto);
    Task<ClientActionResponseDto?> ReviewActionAsync(Guid engagementId, Guid actionId, string tenantId, ReviewActionDto dto);
    Task<ClientActionResponseDto?> ApplyVerificationOutcomeAsync(Guid engagementId, Guid actionId, string tenantId, ApplyActionVerificationDto dto);
    Task<List<ClientActionResponseDto>> EnsureLifecycleActionsAsync(Guid engagementId, string tenantId);

    /// <summary>
    /// IDOR protection (CSTD-22 fix, extended here): confirms the engagement identified by
    /// engagementId+tenantId belongs to clientId. Called by ClientActionsController before any
    /// endpoint a Client-role caller can reach, mirroring RequirementService's
    /// OwnsEngagementAsync — Owner/Staff callers never need this check since they legitimately
    /// act across the whole tenant.
    /// </summary>
    Task<bool> ClientOwnsEngagementAsync(Guid engagementId, string tenantId, string clientId);
}

