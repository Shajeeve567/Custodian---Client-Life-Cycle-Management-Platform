using Custodian.Audit.Models;

namespace Custodian.Audit.Repositories;

public interface IAuditEventRepository
{
    Task<AuditEvent> AddAsync(AuditEvent auditEvent);
    Task<AuditEvent?> GetByIdAsync(Guid eventId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByEngagementIdAsync(Guid engagementId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByTenantIdAsync(Guid tenantId);
}
