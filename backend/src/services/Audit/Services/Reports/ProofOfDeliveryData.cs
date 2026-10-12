namespace Custodian.Audit.Services.Reports;

using Custodian.Audit.Services.EngagementAccess;

/// <summary>
/// CSTD-246: Immutable timeline event snapshot item for Proof-of-Delivery Report.
/// Preserves stored sequence number, actor, type, payload, timestamp, and full hash chain evidence.
/// </summary>
public sealed record ProofOfDeliveryTimelineEvent
{
    public required Guid EventId { get; init; }
    public required Guid EngagementId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Actor { get; init; }
    public required string Type { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Payload { get; init; }
    public required long SequenceNumber { get; init; }
    public required string? Hash { get; init; }
    public required string? PreviousHash { get; init; }
}

/// <summary>
/// CSTD-246: Boundaries of the verified audit event history snapshot.
/// Uses actual stored sequence numbers and hashes rather than synthetic 1..N assumptions.
/// </summary>
public sealed record ProofOfDeliveryHistoryBoundary
{
    public required bool IsEmpty { get; init; }
    public required int EventCount { get; init; }
    public required long? FirstSequenceNumber { get; init; }
    public required long? LastSequenceNumber { get; init; }
    public required Guid? FirstEventId { get; init; }
    public required Guid? LastEventId { get; init; }
    public required DateTime? FirstEventTimestamp { get; init; }
    public required DateTime? LastEventTimestamp { get; init; }
    public required string? GenesisPreviousHash { get; init; }
    public required string? ChainTipHash { get; init; }
    public required DateTime SnapshotTimestampUtc { get; init; }

    public static ProofOfDeliveryHistoryBoundary Empty(DateTime snapshotTimestampUtc) => new()
    {
        IsEmpty = true,
        EventCount = 0,
        FirstSequenceNumber = null,
        LastSequenceNumber = null,
        FirstEventId = null,
        LastEventId = null,
        FirstEventTimestamp = null,
        LastEventTimestamp = null,
        GenesisPreviousHash = null,
        ChainTipHash = null,
        SnapshotTimestampUtc = snapshotTimestampUtc
    };

    public static ProofOfDeliveryHistoryBoundary Create(
        IReadOnlyList<ProofOfDeliveryTimelineEvent> events,
        DateTime snapshotTimestampUtc)
    {
        if (events == null || events.Count == 0)
        {
            return Empty(snapshotTimestampUtc);
        }

        var first = events[0];
        var last = events[events.Count - 1];

        return new ProofOfDeliveryHistoryBoundary
        {
            IsEmpty = false,
            EventCount = events.Count,
            FirstSequenceNumber = first.SequenceNumber,
            LastSequenceNumber = last.SequenceNumber,
            FirstEventId = first.EventId,
            LastEventId = last.EventId,
            FirstEventTimestamp = first.Timestamp,
            LastEventTimestamp = last.Timestamp,
            GenesisPreviousHash = first.PreviousHash,
            ChainTipHash = last.Hash,
            SnapshotTimestampUtc = snapshotTimestampUtc
        };
    }
}

/// <summary>
/// CSTD-246: Aggregate history query result for Proof-of-Delivery Report.
/// Contains confirmed Workflow engagement metadata, immutable event timeline snapshot,
/// and exact history boundaries.
/// </summary>
public sealed record ProofOfDeliveryAggregateData
{
    public required WorkflowEngagementSummary Engagement { get; init; }
    public required ProofOfDeliveryHistoryBoundary HistoryBoundary { get; init; }
    public required IReadOnlyList<ProofOfDeliveryTimelineEvent> Events { get; init; }

    public bool IsEmpty => HistoryBoundary.IsEmpty;
    public int TotalEvents => HistoryBoundary.EventCount;
}

/// <summary>
/// CSTD-247: Outcome of live cryptographic hash-chain verification.
/// Explicitly distinguishes between Valid, Empty, Tampered, and Unavailable.
/// </summary>
public enum ProofOfDeliveryIntegrityStatus
{
    /// <summary>All events in the chain are cryptographically valid from genesis to chain tip.</summary>
    Valid,

    /// <summary>The engagement exists but has 0 audit events. The chain is clean, but no delivery actions have occurred.</summary>
    Empty,

    /// <summary>A cryptographic hash mismatch, broken link, or altered payload/timestamp was detected.</summary>
    Tampered,

    /// <summary>Verification could not be performed due to service or database dependency unavailability.</summary>
    Unavailable
}

/// <summary>
/// CSTD-247: Result of live cryptographic verification for Proof-of-Delivery Report.
/// Contains verified boundary, tip hashes, broken event details (if tampered), and delivery evidence flag.
/// </summary>
public sealed record ProofOfDeliveryVerificationResult
{
    public required ProofOfDeliveryIntegrityStatus Status { get; init; }
    public required int VerifiedEventCount { get; init; }
    public required long? FirstSequenceNumber { get; init; }
    public required long? LastSequenceNumber { get; init; }
    public required Guid? FirstEventId { get; init; }
    public required Guid? LastEventId { get; init; }
    public required DateTime? FirstEventTimestamp { get; init; }
    public required DateTime? LastEventTimestamp { get; init; }
    public required string GenesisHash { get; init; }
    public required string? StoredChainTipHash { get; init; }
    public required DateTime VerifiedAtUtc { get; init; }
    public required bool IsConsistentWithLiveHead { get; init; }

    public Guid? BrokenAtEventId { get; init; }
    public long? BrokenAtSequenceNumber { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>
    /// CSTD-247/CSTD-248: True when the chain is cryptographically valid AND contains at least one verified audit event.
    /// Distinguishes empty chains from intact recorded audit history.
    /// Note: Cryptographic verification demonstrates the tamper-evident integrity of recorded audit history;
    /// it does not imply that engagement delivery or lifecycle completion has occurred.
    /// </summary>
    public bool HasAuditEvidence => Status == ProofOfDeliveryIntegrityStatus.Valid && VerifiedEventCount > 0;

    /// <summary>
    /// CSTD-247/CSTD-248: True ONLY when the chain is cryptographically valid AND contains an explicit, established
    /// delivery-confirmation domain event (e.g. ProofOfDelivery, Delivery).
    /// Does not infer delivery confirmation merely from an engagement being Closed, lifecycle stage transitions,
    /// or the presence of audit events.
    /// </summary>
    public required bool HasDeliveryEvidence { get; init; }

    public bool IsValid => Status == ProofOfDeliveryIntegrityStatus.Valid;
    public bool IsTampered => Status == ProofOfDeliveryIntegrityStatus.Tampered;
    public bool IsEmpty => Status == ProofOfDeliveryIntegrityStatus.Empty;
    public bool IsUnavailable => Status == ProofOfDeliveryIntegrityStatus.Unavailable;
}

/// <summary>
/// CSTD-247: Combined report data containing the aggregate timeline and its live verification outcome.
/// </summary>
public sealed record ProofOfDeliveryVerifiedReportData
{
    public required ProofOfDeliveryAggregateData Aggregate { get; init; }
    public required ProofOfDeliveryVerificationResult Verification { get; init; }

    public bool IsValid => Verification.IsValid;
    public bool HasAuditEvidence => Verification.HasAuditEvidence;
    public bool HasDeliveryEvidence => Verification.HasDeliveryEvidence;
}
