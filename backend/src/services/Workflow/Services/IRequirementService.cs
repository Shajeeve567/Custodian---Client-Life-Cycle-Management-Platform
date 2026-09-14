using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IRequirementService
{
    /// <summary>
    /// <paramref name="callerClientId"/>: when non-null, the caller is a Client-role user and
    /// the result is constrained to engagements that client actually owns (IDOR protection,
    /// mirroring ClientPortalController's ownership check). Pass null for Owner/Staff callers,
    /// who legitimately act across the whole tenant.
    /// </summary>
    Task<IEnumerable<RequirementResponseDto>> GetRequirementsByEngagementAsync(Guid engagementId, string tenantId, bool isClientView, string? callerClientId = null);
    Task<RequirementResponseDto> RequestRequirementAsync(Guid engagementId, string tenantId, RequestRequirementDto dto);
    /// <summary>See <paramref name="callerClientId"/> note on GetRequirementsByEngagementAsync.</summary>
    Task<RequirementResponseDto?> SubmitRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, SubmitRequirementDto dto, string? callerClientId = null);
    Task<RequirementResponseDto?> ReviewRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, ReviewRequirementDto dto);
}
