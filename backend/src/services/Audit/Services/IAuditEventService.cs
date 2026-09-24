using Custodian.Audit.DTOs;

namespace Custodian.Audit.Services;

public interface IAuditEventService
{
    Task<AuditEventResponse> RecordEventAsync(CreateAuditEventRequest request, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByEngagementAsync(Guid engagementId, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByTenantAsync(Guid effectiveTenantId);
    Task<AuditEventResponse?> GetEventByIdAsync(Guid eventId, Guid effectiveTenantId);

    /// <summary>
    /// Recomputes the hash chain for a single engagement and reports whether it
    /// is intact. Chains are scoped per (tenant, engagement), so each engagement
    /// starts from genesis independently.
    /// </summary>
    Task<ChainVerificationResult> VerifyChainAsync(Guid effectiveTenantId, Guid engagementId);
}