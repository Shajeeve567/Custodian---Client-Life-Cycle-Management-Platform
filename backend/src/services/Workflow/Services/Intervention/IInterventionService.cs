using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IInterventionService
{
    Task<InterventionResponse> RecordAsync(
        Guid engagementId,
        string tenantId,
        string actor,
        RecordInterventionRequest request,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<InterventionResponse>> ListForEngagementAsync(
        Guid engagementId,
        string tenantId,
        CancellationToken ct = default
    );
}