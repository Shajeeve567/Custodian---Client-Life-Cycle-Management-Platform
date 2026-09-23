using Custodian.Audit.DTOs;

namespace Custodian.Audit.Services;

public interface IAuditEventService
{
    Task<AuditEventResponse> RecordEventAsync(CreateAuditEventRequest request, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByEngagementAsync(Guid engagementId, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByTenantAsync(Guid effectiveTenantId);
    Task<AuditEventResponse?> GetEventByIdAsync(Guid eventId, Guid effectiveTenantId);

    /// <summary>
    /// Recomputes the tenant's hash chain from scratch
    /// Reports whether it is intact
    /// Reads every event for the tenant in sequence order
    /// Returns the first event that fails verification if any
    /// </summary>
    Task<ChainVerificationResult> VerifyChainAsync(Guid effectiveTenantId);
}
