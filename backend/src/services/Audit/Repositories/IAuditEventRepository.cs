using Custodian.Audit.Models;

namespace Custodian.Audit.Repositories;

public interface IAuditEventRepository
{
    Task<AuditEvent> AddAsync(AuditEvent auditEvent);
    Task<AuditEvent?> GetByIdAsync(Guid eventId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByEngagementIdAsync(Guid engagementId, Guid tenantId);
    Task<IEnumerable<AuditEvent>> GetByTenantIdAsync(Guid tenantId);

    /// <summary>
    /// Returns the engagement's events in chain order (sequence_number ascending).
    /// Used by chain verification. Chain scope is (tenant, engagement) so each
    /// engagement starts from its own genesis.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetByEngagementIdInChainOrderAsync(Guid tenantId, Guid engagementId);

    /// <summary>
    /// Returns the engagement's most recent event (highest sequence_number), or
    /// null if the engagement has no events yet. Used to obtain the previous
    /// hash when appending a new event to this engagement's chain.
    /// </summary>
    Task<AuditEvent?> GetLatestForEngagementAsync(Guid tenantId, Guid engagementId);
}