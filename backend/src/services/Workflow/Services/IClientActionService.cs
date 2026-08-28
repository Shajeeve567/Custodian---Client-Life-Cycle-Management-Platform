using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IClientActionService
{
    Task<IEnumerable<ClientActionResponseDto>> GetActionsByEngagementAsync(Guid engagementId, string tenantId, bool isClientView, string? statusFilter = null);
    Task<ClientActionResponseDto> CreateActionAsync(Guid engagementId, string tenantId, CreateClientActionDto dto);
    Task<ClientActionResponseDto?> CompleteActionAsync(Guid engagementId, Guid actionId, string tenantId, CompleteClientActionDto dto);
}
