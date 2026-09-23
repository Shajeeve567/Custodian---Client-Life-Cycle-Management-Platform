using Custodian.Audit.Models;

namespace Custodian.Audit.Repositories;

public interface IAuditEventRepository
{
    Task<AuditEvent> AddAsync(AuditEvent auditEvent);
    Task<AuditEvent?> GetByIdAsync(Guid eventId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByEngagementIdAsync(Guid engagementId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByTenantIdAsync(Guid tenantId);

    /// <summary>
    /// Returns the tenant's events in chain order
    /// Used by chain verification
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetByTenantIdInChainOrderAsync(Guid tenantId);

    /// <summary>
    /// Returns the tenant's most recent event
    /// null if the tenant has no event yet
    /// Used to obtain the previous hash when appending a new event
    /// </summary>
    Task<AuditEvent?> GetLatestForTenantAsync(Guid tenantId);
}
