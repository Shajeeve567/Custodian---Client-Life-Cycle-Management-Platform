namespace Custodian.Audit.Services.Reports;

using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Custodian.Shared.Reporting.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// CSTD-246 & CSTD-247: Implementation of aggregate history query and live cryptographic hash-chain verification
/// for Proof-of-Delivery Report. Aggregates confirmed Workflow engagement metadata, extracts immutable event
/// snapshots, and validates SHA-256 hash chains at report-generation time.
/// </summary>
public class ProofOfDeliveryReportService : IProofOfDeliveryReportService
{
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IEngagementAccessClient _engagementAccessClient;
    private readonly IHashChainService _hashChainService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProofOfDeliveryReportService> _logger;

    public ProofOfDeliveryReportService(
        IAuditEventRepository auditEventRepository,
        IEngagementAccessClient engagementAccessClient,
        IHashChainService hashChainService,
        TimeProvider? timeProvider = null,
        ILogger<ProofOfDeliveryReportService>? logger = null)
    {
        _auditEventRepository = auditEventRepository ?? throw new ArgumentNullException(nameof(auditEventRepository));
        _engagementAccessClient = engagementAccessClient ?? throw new ArgumentNullException(nameof(engagementAccessClient));
        _hashChainService = hashChainService ?? throw new ArgumentNullException(nameof(hashChainService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProofOfDeliveryReportService>.Instance;
    }

    public async Task<ProofOfDeliveryAggregateData> GetAggregateHistoryAsync(
        Guid tenantId,
        Guid engagementId,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId is required and cannot be empty.", nameof(tenantId));
        }

        if (engagementId == Guid.Empty)
        {
            throw new ArgumentException("EngagementId is required and cannot be empty.", nameof(engagementId));
        }

        // 1. Authenticated access & existence check via Workflow
        WorkflowEngagementSummary? engagement;
        try
        {
            engagement = await _engagementAccessClient.GetEngagementSummaryAsync(engagementId, tenantId, ct);
        }
        catch (EngagementAccessUnavailableException ex)
        {
            _logger.LogWarning(ex, "Workflow service unavailable while retrieving engagement summary for {EngagementId}", engagementId);
            throw ReportGenerationException.DataSourceUnavailable("Workflow service is unreachable. Unable to verify engagement access.", ex);
        }

        if (engagement == null)
        {
            // Fail closed: Engagement does not exist or caller is unauthorized
            throw ReportGenerationException.SubjectNotFound($"Engagement '{engagementId}' was not found in the tenant.");
        }

        // Defensive validation: ensure returned engagement strictly matches requested ID and tenant
        if (engagement.EngagementId != engagementId ||
            !Guid.TryParse(engagement.TenantId, out var engagementTenant) ||
            engagementTenant != tenantId)
        {
            _logger.LogWarning("Workflow engagement response {ReturnedId} (tenant: {ReturnedTenant}) inconsistent with requested {ReqId} (tenant: {ReqTenant})",
                engagement.EngagementId, engagement.TenantId, engagementId, tenantId);
            throw ReportGenerationException.Forbidden("Engagement tenant identity does not match authenticated caller tenant.");
        }

        // 2. Snapshot timestamp
        var snapshotTimestampUtc = _timeProvider.GetUtcNow().UtcDateTime;

        // 3. Query audit events ordered by sequence number within tenant boundaries
        var rawEvents = await _auditEventRepository.GetByEngagementIdInChainOrderAsync(tenantId, engagementId);

        var timelineEvents = rawEvents.Select(e => new ProofOfDeliveryTimelineEvent
        {
            EventId = e.EventId,
            EngagementId = e.EngagementId,
            TenantId = e.TenantId,
            Actor = e.Actor,
            Type = e.Type,
            Timestamp = e.Timestamp,
            Payload = e.Payload,
            SequenceNumber = e.SequenceNumber,
            Hash = e.Hash,
            PreviousHash = e.PreviousHash
        }).ToList();

        // 4. Compute history boundary from real sequence numbers and stored hashes
        var boundary = ProofOfDeliveryHistoryBoundary.Create(timelineEvents, snapshotTimestampUtc);

        return new ProofOfDeliveryAggregateData
        {
            Engagement = engagement,
            HistoryBoundary = boundary,
            Events = timelineEvents
        };
    }

    public ProofOfDeliveryVerificationResult VerifyHistory(
        ProofOfDeliveryAggregateData aggregateData,
        bool isConsistentWithLiveHead = true)
    {
        if (aggregateData == null)
        {
            throw new ArgumentNullException(nameof(aggregateData));
        }

        var verifiedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var events = aggregateData.Events;

        // Case 1: Empty chain (0 events recorded)
        // Explicitly distinguished from valid delivery evidence
        if (events.Count == 0)
        {
            return new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Empty,
                VerifiedEventCount = 0,
                FirstSequenceNumber = null,
                LastSequenceNumber = null,
                FirstEventId = null,
                LastEventId = null,
                FirstEventTimestamp = null,
                LastEventTimestamp = null,
                GenesisHash = _hashChainService.GenesisHash,
                StoredChainTipHash = null,
                VerifiedAtUtc = verifiedAtUtc,
                IsConsistentWithLiveHead = isConsistentWithLiveHead,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            };
        }

        // Case 2: Verification of event snapshot from genesis
        var expectedPreviousHash = _hashChainService.GenesisHash;

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];

            // 1. Verify previous_hash link
            if (!string.Equals(e.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                _logger.LogWarning("Hash chain link broken at event {EventId} (sequence {SequenceNumber}). Expected previous hash '{Expected}', actual '{Actual}'",
                    e.EventId, e.SequenceNumber, expectedPreviousHash, e.PreviousHash);

                return new ProofOfDeliveryVerificationResult
                {
                    Status = ProofOfDeliveryIntegrityStatus.Tampered,
                    VerifiedEventCount = i,
                    FirstSequenceNumber = i > 0 ? events[0].SequenceNumber : null,
                    LastSequenceNumber = i > 0 ? events[i - 1].SequenceNumber : null,
                    FirstEventId = i > 0 ? events[0].EventId : null,
                    LastEventId = i > 0 ? events[i - 1].EventId : null,
                    FirstEventTimestamp = i > 0 ? events[0].Timestamp : null,
                    LastEventTimestamp = i > 0 ? events[i - 1].Timestamp : null,
                    GenesisHash = _hashChainService.GenesisHash,
                    StoredChainTipHash = i > 0 ? events[i - 1].Hash : null,
                    VerifiedAtUtc = verifiedAtUtc,
                    IsConsistentWithLiveHead = false,
                    HasDeliveryEvidence = false,
                    BrokenAtEventId = e.EventId,
                    BrokenAtSequenceNumber = e.SequenceNumber,
                    FailureReason = i == 0
                        ? "First event previous_hash does not match the genesis hash."
                        : $"Event at sequence {e.SequenceNumber} previous_hash does not match the prior event's hash."
                };
            }

            // 2. Recompute and verify event SHA-256 hash in constant time
            var hashInput = new EventHashInput
            {
                EventId = e.EventId,
                EngagementId = e.EngagementId,
                TenantId = e.TenantId,
                Actor = e.Actor,
                Type = e.Type,
                Timestamp = e.Timestamp,
                Payload = e.Payload,
                PreviousHash = e.PreviousHash ?? string.Empty
            };

            if (!_hashChainService.VerifyEventHash(hashInput, e.Hash ?? string.Empty))
            {
                _logger.LogWarning("Stored SHA-256 hash mismatch at event {EventId} (sequence {SequenceNumber})", e.EventId, e.SequenceNumber);

                return new ProofOfDeliveryVerificationResult
                {
                    Status = ProofOfDeliveryIntegrityStatus.Tampered,
                    VerifiedEventCount = i,
                    FirstSequenceNumber = i > 0 ? events[0].SequenceNumber : null,
                    LastSequenceNumber = i > 0 ? events[i - 1].SequenceNumber : null,
                    FirstEventId = i > 0 ? events[0].EventId : null,
                    LastEventId = i > 0 ? events[i - 1].EventId : null,
                    FirstEventTimestamp = i > 0 ? events[0].Timestamp : null,
                    LastEventTimestamp = i > 0 ? events[i - 1].Timestamp : null,
                    GenesisHash = _hashChainService.GenesisHash,
                    StoredChainTipHash = i > 0 ? events[i - 1].Hash : null,
                    VerifiedAtUtc = verifiedAtUtc,
                    IsConsistentWithLiveHead = false,
                    HasDeliveryEvidence = false,
                    BrokenAtEventId = e.EventId,
                    BrokenAtSequenceNumber = e.SequenceNumber,
                    FailureReason = $"Stored hash does not match recomputed SHA-256 hash at sequence {e.SequenceNumber} (Event ID '{e.EventId}')."
                };
            }

            expectedPreviousHash = e.Hash!;
        }

        // All events in the snapshot verified successfully
        var firstEvent = events[0];
        var lastEvent = events[events.Count - 1];

        return new ProofOfDeliveryVerificationResult
        {
            Status = ProofOfDeliveryIntegrityStatus.Valid,
            VerifiedEventCount = events.Count,
            FirstSequenceNumber = firstEvent.SequenceNumber,
            LastSequenceNumber = lastEvent.SequenceNumber,
            FirstEventId = firstEvent.EventId,
            LastEventId = lastEvent.EventId,
            FirstEventTimestamp = firstEvent.Timestamp,
            LastEventTimestamp = lastEvent.Timestamp,
            GenesisHash = _hashChainService.GenesisHash,
            StoredChainTipHash = lastEvent.Hash,
            VerifiedAtUtc = verifiedAtUtc,
            IsConsistentWithLiveHead = isConsistentWithLiveHead,
            HasDeliveryEvidence = HasEstablishedDeliveryEvidence(aggregateData, events.Count),
            BrokenAtEventId = null,
            BrokenAtSequenceNumber = null,
            FailureReason = null
        };
    }

    public async Task<ProofOfDeliveryVerifiedReportData> GetVerifiedReportDataAsync(
        Guid tenantId,
        Guid engagementId,
        CancellationToken ct = default)
    {
        // 1. Fetch aggregate history snapshot (CSTD-246)
        var aggregateData = await GetAggregateHistoryAsync(tenantId, engagementId, ct);

        // 2. Check for concurrent appends against live database tip
        bool isConsistentWithLiveHead = true;
        try
        {
            var latestDbEvent = await _auditEventRepository.GetLatestForEngagementAsync(tenantId, engagementId);

            if (!CheckLiveHeadConsistency(latestDbEvent, aggregateData.HistoryBoundary))
            {
                _logger.LogInformation(
                    "Snapshot head inconsistent with live DB tip for engagement {EngagementId} (DB tip seq: {DbSeq}, snapshot tip seq: {SnapSeq}). Refreshing snapshot once.",
                    engagementId, latestDbEvent?.SequenceNumber, aggregateData.HistoryBoundary.LastSequenceNumber);

                // Re-fetch snapshot once to include latest events if possible
                aggregateData = await GetAggregateHistoryAsync(tenantId, engagementId, ct);
                var recheckedDbEvent = await _auditEventRepository.GetLatestForEngagementAsync(tenantId, engagementId);

                isConsistentWithLiveHead = CheckLiveHeadConsistency(recheckedDbEvent, aggregateData.HistoryBoundary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query latest event tip for engagement {EngagementId}. Proceeding with snapshot verification.", engagementId);
            isConsistentWithLiveHead = false;
        }

        // 3. Cryptographically verify the exact snapshot in memory (CSTD-247)
        ProofOfDeliveryVerificationResult verification;
        try
        {
            verification = VerifyHistory(aggregateData, isConsistentWithLiveHead);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure during live hash-chain verification for engagement {EngagementId}", engagementId);
            verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Unavailable,
                VerifiedEventCount = 0,
                FirstSequenceNumber = null,
                LastSequenceNumber = null,
                FirstEventId = null,
                LastEventId = null,
                FirstEventTimestamp = null,
                LastEventTimestamp = null,
                GenesisHash = _hashChainService.GenesisHash,
                StoredChainTipHash = null,
                VerifiedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                IsConsistentWithLiveHead = false,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = $"Verification unavailable: {ex.Message}"
            };
        }

        return new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = aggregateData,
            Verification = verification
        };
    }

    public async Task<ProofOfDeliveryReport> BuildReportAsync(
        Guid tenantId,
        Guid engagementId,
        string generatedBy,
        CancellationToken ct = default)
    {
        var verifiedData = await GetVerifiedReportDataAsync(tenantId, engagementId, ct);
        return ProofOfDeliveryReportBuilder.Build(verifiedData, tenantId.ToString(), generatedBy);
    }

    private static bool CheckLiveHeadConsistency(
        AuditEvent? latestDbEvent,
        ProofOfDeliveryHistoryBoundary boundary)
    {
        if (latestDbEvent == null && boundary.IsEmpty)
        {
            return true;
        }

        if (latestDbEvent == null || boundary.IsEmpty)
        {
            return false;
        }

        return latestDbEvent.SequenceNumber == boundary.LastSequenceNumber &&
               latestDbEvent.EventId == boundary.LastEventId &&
               string.Equals(latestDbEvent.Hash, boundary.ChainTipHash, StringComparison.Ordinal);
    }

    private static bool HasEstablishedDeliveryEvidence(
        ProofOfDeliveryAggregateData aggregateData,
        int verifiedCount)
    {
        if (verifiedCount <= 0)
        {
            return false;
        }

        var events = aggregateData.Events;
        for (var i = 0; i < verifiedCount && i < events.Count; i++)
        {
            var e = events[i];

            // Only claim confirmed delivery where a real, established domain event explicitly supports that conclusion.
            // Do not infer confirmed delivery merely from an engagement being Closed, an ordinary lifecycle transition,
            // or the presence of audit events.
            if (string.Equals(e.Type, "ProofOfDelivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Type, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Type, "Delivered", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
