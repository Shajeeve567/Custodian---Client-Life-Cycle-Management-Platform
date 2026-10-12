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

    /// <summary>
    /// CSTD-42: Flags an audit event with the supplied reason and authenticated actor.
    /// Appends an immutable AuditEventFlagged reference event to the chain exactly once.
    /// Idempotent: repeated calls do not append another event or advance the chain head.
    /// </summary>
    Task<AuditEventResponse?> FlagEventAsync(Guid eventId, Guid effectiveTenantId, string reason, string actor);

    /// <summary>
    /// CSTD-42: Archives an audit event with an optional reason and authenticated actor.
    /// Idempotent: repeated calls do not append an event or mutate chain verification state.
    /// </summary>
    Task<AuditEventResponse?> ArchiveEventAsync(Guid eventId, Guid effectiveTenantId, string? reason, string actor);
}