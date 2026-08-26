using Custodian.Audit.DTOs;

namespace Custodian.Audit.Services;

public interface IAuditEventService
{
    Task<AuditEventResponse> RecordEventAsync(CreateAuditEventRequest request, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByEngagementAsync(Guid engagementId, Guid effectiveTenantId);
    Task<IEnumerable<AuditEventResponse>> GetEventsByTenantAsync(Guid effectiveTenantId);
    Task<AuditEventResponse?> GetEventByIdAsync(Guid eventId, Guid effectiveTenantId);
}
