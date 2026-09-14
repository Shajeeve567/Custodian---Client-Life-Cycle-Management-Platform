using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IRequirementService
{
    Task<IEnumerable<RequirementResponseDto>> GetRequirementsByEngagementAsync(Guid engagementId, string tenantId, bool isClientView);
    Task<RequirementResponseDto> RequestRequirementAsync(Guid engagementId, string tenantId, RequestRequirementDto dto);
    Task<RequirementResponseDto?> SubmitRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, SubmitRequirementDto dto);
    Task<RequirementResponseDto?> ReviewRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, ReviewRequirementDto dto);
}
