namespace Custodian.Audit.Tests.Unit.Reports;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Custodian.Audit.Services.Reports;
using Custodian.Shared.Reporting.Errors;
using Moq;
using Xunit;

public class ProofOfDeliveryReportServiceTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();
    private readonly Mock<IAuditEventRepository> _repositoryMock = new();
    private readonly Mock<IEngagementAccessClient> _accessClientMock = new();
    private readonly IHashChainService _hashChainService = new HashChainService();
    private readonly DateTime _fixedNow = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeProvider _timeProvider;

    public ProofOfDeliveryReportServiceTests()
    {
        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(t => t.GetUtcNow()).Returns(new DateTimeOffset(_fixedNow));
        _timeProvider = timeProviderMock.Object;
    }

    private WorkflowEngagementSummary CreateSampleEngagementSummary(Guid? engagementId = null, Guid? tenantId = null) => new()
    {
        EngagementId = engagementId ?? _engagementId,
        TenantId = (tenantId ?? _tenantId).ToString(),
        ClientId = Guid.NewGuid().ToString(),
        StaffId = "staff-user-1",
        Status = "InProgress",
        Stage = "Execution",
        StageProgressPercentage = 75,
        CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        ClosedAt = null
    };

    private (AuditEvent e1, AuditEvent e2, AuditEvent e3) CreateValidThreeEventChain()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var id1 = Guid.NewGuid();
        var prev1 = HashChainService.Genesis;
        var input1 = new EventHashInput
        {
            EventId = id1,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "system",
            Type = "EngagementCreated",
            Timestamp = now,
            Payload = "{\"message\":\"created\"}",
            PreviousHash = prev1
        };
        var hash1 = _hashChainService.ComputeEventHash(input1);
        var e1 = new AuditEvent
        {
            EventId = id1,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "system",
            Type = "EngagementCreated",
            Timestamp = now,
            Payload = "{\"message\":\"created\"}",
            SequenceNumber = 101,
            PreviousHash = prev1,
            Hash = hash1
        };

        var id2 = Guid.NewGuid();
        var now2 = now.AddDays(2);
        var input2 = new EventHashInput
        {
            EventId = id2,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "staff-1",
            Type = "StageAdvanced",
            Timestamp = now2,
            Payload = "{\"stage\":\"Verification\"}",
            PreviousHash = hash1
        };
        var hash2 = _hashChainService.ComputeEventHash(input2);
        var e2 = new AuditEvent
        {
            EventId = id2,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "staff-1",
            Type = "StageAdvanced",
            Timestamp = now2,
            Payload = "{\"stage\":\"Verification\"}",
            SequenceNumber = 102,
            PreviousHash = hash1,
            Hash = hash2
        };

        var id3 = Guid.NewGuid();
        var now3 = now.AddDays(4);
        var input3 = new EventHashInput
        {
            EventId = id3,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "client-1",
            Type = "ApprovalCompleted",
            Timestamp = now3,
            Payload = "{\"decision\":\"Approved\"}",
            PreviousHash = hash2
        };
        var hash3 = _hashChainService.ComputeEventHash(input3);
        var e3 = new AuditEvent
        {
            EventId = id3,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "client-1",
            Type = "ApprovalCompleted",
            Timestamp = now3,
            Payload = "{\"decision\":\"Approved\"}",
            SequenceNumber = 105,
            PreviousHash = hash2,
            Hash = hash3
        };

        return (e1, e2, e3);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_ValidEngagementWithEvents_ReturnsCompleteAggregateAndRealBoundary()
    {
        // Arrange
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        // Act
        var result = await sut.GetAggregateHistoryAsync(_tenantId, _engagementId);

        // Assert
        Assert.NotNull(result);
        Assert.Same(engagement, result.Engagement);
        Assert.False(result.IsEmpty);
        Assert.Equal(3, result.TotalEvents);

        // Events list assertions
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(101, result.Events[0].SequenceNumber);
        Assert.Equal(102, result.Events[1].SequenceNumber);
        Assert.Equal(105, result.Events[2].SequenceNumber);
        Assert.Equal("EngagementCreated", result.Events[0].Type);
        Assert.Equal("ApprovalCompleted", result.Events[2].Type);

        // History Boundary assertions (actual sequence and hash values)
        var boundary = result.HistoryBoundary;
        Assert.False(boundary.IsEmpty);
        Assert.Equal(3, boundary.EventCount);
        Assert.Equal(101, boundary.FirstSequenceNumber);
        Assert.Equal(105, boundary.LastSequenceNumber);
        Assert.Equal(e1.EventId, boundary.FirstEventId);
        Assert.Equal(e3.EventId, boundary.LastEventId);
        Assert.Equal(e1.Timestamp, boundary.FirstEventTimestamp);
        Assert.Equal(e3.Timestamp, boundary.LastEventTimestamp);
        Assert.Equal(HashChainService.Genesis, boundary.GenesisPreviousHash);
        Assert.Equal(e3.Hash, boundary.ChainTipHash);
        Assert.Equal(_fixedNow, boundary.SnapshotTimestampUtc);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_EngagementWithZeroEvents_ReturnsEmptyHistoryBoundary()
    {
        // Arrange
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent>());

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        // Act
        var result = await sut.GetAggregateHistoryAsync(_tenantId, _engagementId);

        // Assert
        Assert.NotNull(result);
        Assert.Same(engagement, result.Engagement);
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.TotalEvents);
        Assert.Empty(result.Events);

        var boundary = result.HistoryBoundary;
        Assert.True(boundary.IsEmpty);
        Assert.Equal(0, boundary.EventCount);
        Assert.Null(boundary.FirstSequenceNumber);
        Assert.Null(boundary.LastSequenceNumber);
        Assert.Null(boundary.FirstEventId);
        Assert.Null(boundary.LastEventId);
        Assert.Null(boundary.FirstEventTimestamp);
        Assert.Null(boundary.LastEventTimestamp);
        Assert.Null(boundary.GenesisPreviousHash);
        Assert.Null(boundary.ChainTipHash);
        Assert.Equal(_fixedNow, boundary.SnapshotTimestampUtc);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_EngagementNotFoundInWorkflow_ThrowsSubjectNotFound()
    {
        // Arrange: Workflow returns null (404/403/unauthorized)
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowEngagementSummary?)null);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            sut.GetAggregateHistoryAsync(_tenantId, _engagementId));

        Assert.Equal(ReportErrorKind.SubjectNotFound, ex.Kind);
        _repositoryMock.Verify(r => r.GetByEngagementIdInChainOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_WorkflowUnavailable_ThrowsDataSourceUnavailable()
    {
        // Arrange: Workflow throws EngagementAccessUnavailableException (network down, 503)
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EngagementAccessUnavailableException("Workflow unreachable"));

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        // Act & Assert: Fails closed with 503 DataSourceUnavailable
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            sut.GetAggregateHistoryAsync(_tenantId, _engagementId));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
        _repositoryMock.Verify(r => r.GetByEngagementIdInChainOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_WorkflowReturnsMismatchedEngagementId_FailsClosedWithForbidden()
    {
        var otherEngagementId = Guid.NewGuid();
        var mismatchedSummary = CreateSampleEngagementSummary(engagementId: otherEngagementId);
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mismatchedSummary);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            sut.GetAggregateHistoryAsync(_tenantId, _engagementId));

        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
        _repositoryMock.Verify(r => r.GetByEngagementIdInChainOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_WorkflowReturnsMismatchedTenantId_FailsClosedWithForbidden()
    {
        var otherTenantId = Guid.NewGuid();
        var mismatchedSummary = CreateSampleEngagementSummary(tenantId: otherTenantId);
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mismatchedSummary);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            sut.GetAggregateHistoryAsync(_tenantId, _engagementId));

        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
        _repositoryMock.Verify(r => r.GetByEngagementIdInChainOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_EmptyTenantId_ThrowsArgumentException()
    {
        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.GetAggregateHistoryAsync(Guid.Empty, _engagementId));
    }

    [Fact]
    public async Task GetAggregateHistoryAsync_EmptyEngagementId_ThrowsArgumentException()
    {
        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.GetAggregateHistoryAsync(_tenantId, Guid.Empty));
    }

    // =========================================================================
    // CSTD-247 Verification Tests
    // =========================================================================

    [Fact]
    public void VerifyHistory_ValidChain_ReturnsValidStatusWithDeliveryEvidenceAndExactBoundary()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();
        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[]
            {
                MapToTimeline(e1), MapToTimeline(e2), MapToTimeline(e3)
            }, _fixedNow),
            Events = new[] { MapToTimeline(e1), MapToTimeline(e2), MapToTimeline(e3) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.True(result.HasAuditEvidence);
        Assert.False(result.HasDeliveryEvidence); // In-progress engagement with intermediate events has audit evidence, not delivery evidence
        Assert.False(result.IsEmpty);
        Assert.False(result.IsTampered);
        Assert.False(result.IsUnavailable);
        Assert.Equal(3, result.VerifiedEventCount);
        Assert.Equal(101, result.FirstSequenceNumber);
        Assert.Equal(105, result.LastSequenceNumber);
        Assert.Equal(e1.EventId, result.FirstEventId);
        Assert.Equal(e3.EventId, result.LastEventId);
        Assert.Equal(e1.Timestamp, result.FirstEventTimestamp);
        Assert.Equal(e3.Timestamp, result.LastEventTimestamp);
        Assert.Equal(HashChainService.Genesis, result.GenesisHash);
        Assert.Equal(e3.Hash, result.StoredChainTipHash);
        Assert.Equal(_fixedNow, result.VerifiedAtUtc);
        Assert.Null(result.BrokenAtEventId);
        Assert.Null(result.BrokenAtSequenceNumber);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void VerifyHistory_EmptyHistory_ReturnsEmptyStatusAndNoDeliveryEvidence()
    {
        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Empty(_fixedNow),
            Events = Array.Empty<ProofOfDeliveryTimelineEvent>()
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Empty, result.Status);
        Assert.False(result.IsValid);
        Assert.False(result.HasDeliveryEvidence);
        Assert.True(result.IsEmpty);
        Assert.False(result.IsTampered);
        Assert.Equal(0, result.VerifiedEventCount);
        Assert.Null(result.FirstSequenceNumber);
        Assert.Null(result.LastSequenceNumber);
        Assert.Null(result.FirstEventId);
        Assert.Null(result.LastEventId);
        Assert.Null(result.StoredChainTipHash);
        Assert.Equal(HashChainService.Genesis, result.GenesisHash);
        Assert.Null(result.BrokenAtEventId);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void VerifyHistory_TamperedPayload_ReturnsTamperedStatusAndIdentifiesBrokenEvent()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();

        // Tamper event 2 payload without updating hash
        var tamperedEvent2 = new ProofOfDeliveryTimelineEvent
        {
            EventId = e2.EventId,
            EngagementId = e2.EngagementId,
            TenantId = e2.TenantId,
            Actor = e2.Actor,
            Type = e2.Type,
            Timestamp = e2.Timestamp,
            Payload = "{\"stage\":\"MALICIOUS_TAMPER\"}", // Tampered payload
            SequenceNumber = e2.SequenceNumber,
            Hash = e2.Hash, // Old hash mismatching tampered payload
            PreviousHash = e2.PreviousHash
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[]
            {
                MapToTimeline(e1), tamperedEvent2, MapToTimeline(e3)
            }, _fixedNow),
            Events = new[] { MapToTimeline(e1), tamperedEvent2, MapToTimeline(e3) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        Assert.False(result.IsValid);
        Assert.False(result.HasDeliveryEvidence);
        Assert.True(result.IsTampered);
        Assert.Equal(1, result.VerifiedEventCount); // Only event 1 passed before tamper
        Assert.Equal(tamperedEvent2.EventId, result.BrokenAtEventId);
        Assert.Equal(102, result.BrokenAtSequenceNumber);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("recomputed SHA-256 hash", result.FailureReason);
        Assert.Equal(e1.Hash, result.StoredChainTipHash); // Tip before the break
    }

    [Fact]
    public void VerifyHistory_BrokenPreviousHashLink_ReturnsTamperedStatus()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();

        // Broken link on event 3: previous hash does not match event 2's hash
        var brokenLinkEvent3 = new ProofOfDeliveryTimelineEvent
        {
            EventId = e3.EventId,
            EngagementId = e3.EngagementId,
            TenantId = e3.TenantId,
            Actor = e3.Actor,
            Type = e3.Type,
            Timestamp = e3.Timestamp,
            Payload = e3.Payload,
            SequenceNumber = e3.SequenceNumber,
            Hash = e3.Hash,
            PreviousHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" // Broken link
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[]
            {
                MapToTimeline(e1), MapToTimeline(e2), brokenLinkEvent3
            }, _fixedNow),
            Events = new[] { MapToTimeline(e1), MapToTimeline(e2), brokenLinkEvent3 }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        Assert.False(result.IsValid);
        Assert.True(result.IsTampered);
        Assert.Equal(2, result.VerifiedEventCount); // Event 1 and 2 passed
        Assert.Equal(brokenLinkEvent3.EventId, result.BrokenAtEventId);
        Assert.Equal(105, result.BrokenAtSequenceNumber);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("previous_hash does not match", result.FailureReason);
    }

    [Fact]
    public void VerifyHistory_FirstEventNotFromGenesis_ReturnsTamperedStatus()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();

        var nonGenesisEvent1 = new ProofOfDeliveryTimelineEvent
        {
            EventId = e1.EventId,
            EngagementId = e1.EngagementId,
            TenantId = e1.TenantId,
            Actor = e1.Actor,
            Type = e1.Type,
            Timestamp = e1.Timestamp,
            Payload = e1.Payload,
            SequenceNumber = e1.SequenceNumber,
            Hash = e1.Hash,
            PreviousHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[]
            {
                nonGenesisEvent1, MapToTimeline(e2), MapToTimeline(e3)
            }, _fixedNow),
            Events = new[] { nonGenesisEvent1, MapToTimeline(e2), MapToTimeline(e3) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        Assert.Equal(0, result.VerifiedEventCount);
        Assert.Equal(nonGenesisEvent1.EventId, result.BrokenAtEventId);
        Assert.Contains("genesis hash", result.FailureReason);
    }

    [Fact]
    public async Task GetVerifiedReportDataAsync_ValidChain_ReturnsCombinedVerifiedReportData()
    {
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        _repositoryMock
            .Setup(r => r.GetLatestForEngagementAsync(_tenantId, _engagementId))
            .ReturnsAsync(e3);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = await sut.GetVerifiedReportDataAsync(_tenantId, _engagementId);

        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.True(result.HasAuditEvidence);
        Assert.False(result.HasDeliveryEvidence); // In-progress chain has audit evidence, not delivery evidence
        Assert.Equal(3, result.Verification.VerifiedEventCount);
        Assert.Equal(e3.Hash, result.Verification.StoredChainTipHash);
        Assert.True(result.Verification.IsConsistentWithLiveHead);
        Assert.Equal(3, result.Aggregate.TotalEvents);
    }

    [Fact]
    public async Task GetVerifiedReportDataAsync_ConcurrentAppendDetected_RefreshesSnapshotOnce()
    {
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        // First call returns only event 1 and 2 (sequence 102)
        // Second call returns all three events (sequence 105)
        _repositoryMock
            .SetupSequence(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2 })
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        // Database live head is already event 3 (sequence 105)
        _repositoryMock
            .Setup(r => r.GetLatestForEngagementAsync(_tenantId, _engagementId))
            .ReturnsAsync(e3);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = await sut.GetVerifiedReportDataAsync(_tenantId, _engagementId);

        // Verification refreshed to include event 3 and confirmed live head
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.Verification.VerifiedEventCount);
        Assert.Equal(105, result.Verification.LastSequenceNumber);
        Assert.True(result.Verification.IsConsistentWithLiveHead);
    }

    [Fact]
    public async Task GetVerifiedReportDataAsync_VerificationThrowsUnexpectedException_ReturnsUnavailableNeverValid()
    {
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        var failingHashChainMock = new Mock<IHashChainService>();
        failingHashChainMock.Setup(h => h.GenesisHash).Returns(HashChainService.Genesis);
        failingHashChainMock
            .Setup(h => h.VerifyEventHash(It.IsAny<EventHashInput>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("Cryptographic subsystem failure"));

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            failingHashChainMock.Object,
            _timeProvider);

        var result = await sut.GetVerifiedReportDataAsync(_tenantId, _engagementId);

        Assert.NotNull(result);
        Assert.Equal(ProofOfDeliveryIntegrityStatus.Unavailable, result.Verification.Status);
        Assert.False(result.IsValid);
        Assert.False(result.HasDeliveryEvidence);
        Assert.True(result.Verification.IsUnavailable);
        Assert.Contains("Cryptographic subsystem failure", result.Verification.FailureReason);
    }

    [Fact]
    public void VerifyHistory_FirstEventGenesisTampered_ReturnsTamperedWithZeroVerifiedEventsAndNullBoundaries()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();

        // Tamper event 1 previous_hash to not match genesis
        var tamperedEvent1 = new ProofOfDeliveryTimelineEvent
        {
            EventId = e1.EventId,
            EngagementId = e1.EngagementId,
            TenantId = e1.TenantId,
            Actor = e1.Actor,
            Type = e1.Type,
            Timestamp = e1.Timestamp,
            Payload = e1.Payload,
            SequenceNumber = e1.SequenceNumber,
            Hash = e1.Hash,
            PreviousHash = "corrupted_genesis_prev_hash"
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[] { tamperedEvent1, MapToTimeline(e2), MapToTimeline(e3) }, _fixedNow),
            Events = new[] { tamperedEvent1, MapToTimeline(e2), MapToTimeline(e3) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        Assert.False(result.IsValid);
        Assert.False(result.HasDeliveryEvidence);
        Assert.False(result.HasAuditEvidence);
        Assert.Equal(0, result.VerifiedEventCount);
        Assert.Null(result.FirstSequenceNumber);
        Assert.Null(result.LastSequenceNumber);
        Assert.Null(result.FirstEventId);
        Assert.Null(result.LastEventId);
        Assert.Null(result.FirstEventTimestamp);
        Assert.Null(result.LastEventTimestamp);
        Assert.Null(result.StoredChainTipHash);
        Assert.Equal(tamperedEvent1.EventId, result.BrokenAtEventId);
        Assert.Equal(tamperedEvent1.SequenceNumber, result.BrokenAtSequenceNumber);
        Assert.Contains("First event previous_hash does not match the genesis hash", result.FailureReason);
    }

    [Fact]
    public void VerifyHistory_ValidChainWithProofOfDeliveryEvent_ReturnsHasDeliveryEvidenceTrue()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var id1 = Guid.NewGuid();
        var prev1 = HashChainService.Genesis;
        var input1 = new EventHashInput
        {
            EventId = id1,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "system",
            Type = "Genesis",
            Timestamp = now,
            Payload = "{}",
            PreviousHash = prev1
        };
        var hash1 = _hashChainService.ComputeEventHash(input1);
        var e1 = new AuditEvent
        {
            EventId = id1,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "system",
            Type = "Genesis",
            Timestamp = now,
            Payload = "{}",
            SequenceNumber = 1,
            PreviousHash = prev1,
            Hash = hash1
        };

        var id2 = Guid.NewGuid();
        var input2 = new EventHashInput
        {
            EventId = id2,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "staff-1",
            Type = "ProofOfDelivery",
            Timestamp = now.AddDays(1),
            Payload = "{\"delivered\":true}",
            PreviousHash = hash1
        };
        var hash2 = _hashChainService.ComputeEventHash(input2);
        var e2 = new AuditEvent
        {
            EventId = id2,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "staff-1",
            Type = "ProofOfDelivery",
            Timestamp = now.AddDays(1),
            Payload = "{\"delivered\":true}",
            SequenceNumber = 2,
            PreviousHash = hash1,
            Hash = hash2
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = CreateSampleEngagementSummary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[] { MapToTimeline(e1), MapToTimeline(e2) }, _fixedNow),
            Events = new[] { MapToTimeline(e1), MapToTimeline(e2) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.True(result.HasAuditEvidence);
        Assert.True(result.HasDeliveryEvidence);
    }

    [Fact]
    public void VerifyHistory_ValidChainWithClosedEngagement_WithoutExplicitDeliveryEvent_DoesNotInferDeliveryEvidence()
    {
        var (e1, e2, e3) = CreateValidThreeEventChain();

        var closedEngagement = new WorkflowEngagementSummary
        {
            EngagementId = _engagementId,
            TenantId = _tenantId.ToString(),
            ClientId = Guid.NewGuid().ToString(),
            StaffId = "staff-1",
            Status = "Closed",
            Stage = "Closure",
            StageProgressPercentage = 100,
            CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc)
        };

        var aggregate = new ProofOfDeliveryAggregateData
        {
            Engagement = closedEngagement,
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[] { MapToTimeline(e1), MapToTimeline(e2), MapToTimeline(e3) }, _fixedNow),
            Events = new[] { MapToTimeline(e1), MapToTimeline(e2), MapToTimeline(e3) }
        };

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = sut.VerifyHistory(aggregate);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.True(result.HasAuditEvidence);
        Assert.False(result.HasDeliveryEvidence); // Delivery confirmation is not inferred merely from Closed status
    }

    [Fact]
    public async Task GetVerifiedReportDataAsync_LiveHeadHashMismatch_FlagsInconsistentWithLiveHead()
    {
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        // Database tip has same sequence number but different hash (mismatched head)
        var mismatchedTip = new AuditEvent
        {
            EventId = e3.EventId,
            EngagementId = e3.EngagementId,
            TenantId = e3.TenantId,
            Actor = e3.Actor,
            Type = e3.Type,
            Timestamp = e3.Timestamp,
            Payload = e3.Payload,
            SequenceNumber = e3.SequenceNumber,
            PreviousHash = e3.PreviousHash,
            Hash = "different_hash_on_live_head"
        };

        _repositoryMock
            .Setup(r => r.GetLatestForEngagementAsync(_tenantId, _engagementId))
            .ReturnsAsync(mismatchedTip);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var result = await sut.GetVerifiedReportDataAsync(_tenantId, _engagementId);

        Assert.NotNull(result);
        Assert.True(result.IsValid); // Snapshot itself is internally valid
        Assert.False(result.Verification.IsConsistentWithLiveHead); // But live tip hash diverges
    }


    [Fact]
    public async Task BuildReportAsync_ValidData_ReturnsCompleteProofOfDeliveryReport()
    {
        var engagement = CreateSampleEngagementSummary();
        _accessClientMock
            .Setup(c => c.GetEngagementSummaryAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(engagement);

        var (e1, e2, e3) = CreateValidThreeEventChain();

        _repositoryMock
            .Setup(r => r.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId))
            .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });

        _repositoryMock
            .Setup(r => r.GetLatestForEngagementAsync(_tenantId, _engagementId))
            .ReturnsAsync(e3);

        var sut = new ProofOfDeliveryReportService(
            _repositoryMock.Object,
            _accessClientMock.Object,
            _hashChainService,
            _timeProvider);

        var report = await sut.BuildReportAsync(_tenantId, _engagementId, "test-user");

        Assert.NotNull(report);
        Assert.Equal(ProofOfDeliveryReportBuilder.ReportCode, report.Metadata.ReportCode);
        Assert.Equal(ProofOfDeliveryReportBuilder.Title, report.Metadata.Title);
        Assert.Equal("test-user", report.Metadata.GeneratedBy);
        Assert.NotEmpty(report.Sections);
        Assert.NotNull(report.Detail);
        Assert.Equal(3, report.Detail.Rows.Count);
    }

    private static ProofOfDeliveryTimelineEvent MapToTimeline(AuditEvent e) => new()
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
    };
}
