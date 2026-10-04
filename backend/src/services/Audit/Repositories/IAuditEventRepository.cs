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

    /// <summary>
    /// Appends one event to its engagement's chain as a single atomic step: inside one transaction it
    /// checks the event id is new (idempotency), locks the engagement's chain head, builds the event
    /// from the head's hash via <paramref name="buildEvent"/>, inserts it and moves the head. Concurrent
    /// appends to the same engagement are serialised, so the chain never forks.
    /// </summary>
    /// <param name="buildEvent">Builds the event (including its hash) from the previous hash.</param>
    /// <returns>The recorded event, and whether it was created now (false: it already existed).</returns>
    /// <exception cref="AuditChainConflictException">The event id or the engagement's chain belongs to another tenant.</exception>
    Task<ChainAppendResult> AppendToChainAsync(Guid tenantId, Guid engagementId, Guid eventId, Func<string, AuditEvent> buildEvent);

    /// <summary>
    /// CSTD-42 (CSTD-241): Gets non-cryptographic metadata (flag/archive) for an event within the tenant.
    /// </summary>
    Task<AuditEventMetadata?> GetMetadataAsync(Guid eventId, Guid tenantId);

    /// <summary>
    /// CSTD-42 (CSTD-241): Gets non-cryptographic metadata for a batch of events within the tenant.
    /// Keyed by event_id.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AuditEventMetadata>> GetMetadataForEventsAsync(IEnumerable<Guid> eventIds, Guid tenantId);

    /// <summary>
    /// CSTD-42 (CSTD-241): Adds or updates non-cryptographic metadata for an event. Enforces tenant scoping.
    /// </summary>
    Task<AuditEventMetadata> SetMetadataAsync(AuditEventMetadata metadata);
}

public sealed record ChainAppendResult(AuditEvent Event, bool Created);

/// <summary>An append that conflicts with another tenant's event id or chain. Never retried.</summary>
public sealed class AuditChainConflictException : InvalidOperationException
{
    public AuditChainConflictException(string message) : base(message) { }
}