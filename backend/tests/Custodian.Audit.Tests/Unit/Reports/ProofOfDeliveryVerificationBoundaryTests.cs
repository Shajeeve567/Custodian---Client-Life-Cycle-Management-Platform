using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Custodian.Audit.Services.Reports;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit.Reports;

/// <summary>
/// CSTD-247: exactly which part of the chain a proof-of-delivery report may call verified. When the chain
/// breaks at event N, the verified boundary is events 1..N-1 (first/last sequence, ids, timestamps and the
/// tip hash), never the broken event. Also: the live-head consistency check and the delivery evidence rule.
/// </summary>
public class ProofOfDeliveryVerificationBoundaryTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Engagement = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashChainService Hashes = new();

    private readonly Mock<IAuditEventRepository> _repository = new();
    private readonly Mock<IEngagementAccessClient> _access = new();

    public ProofOfDeliveryVerificationBoundaryTests()
    {
        _access.Setup(a => a.GetEngagementSummaryAsync(Engagement, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Summary());
    }

    private static WorkflowEngagementSummary Summary() => new()
    {
        EngagementId = Engagement,
        TenantId = Tenant.ToString(),
        ClientId = "client-1",
        StaffId = "staff-1",
        Status = "Started",
        Stage = "Execution",
        StageProgressPercentage = 75,
        CreatedAt = Now.AddDays(-30)
    };

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private ProofOfDeliveryReportService Service => new(_repository.Object, _access.Object, Hashes, new FixedTime());

    /// <summary>A correctly hashed chain: sequence 11, 12, 13, ... one day apart.</summary>
    private static List<AuditEvent> Chain(params string[] types)
    {
        var events = new List<AuditEvent>();
        var previous = HashChainService.Genesis;
        for (var i = 0; i < types.Length; i++)
        {
            var e = new AuditEvent
            {
                EventId = Guid.NewGuid(),
                EngagementId = Engagement,
                TenantId = Tenant,
                Actor = "staff-1",
                Type = types[i],
                Timestamp = Now.AddDays(-10 + i),
                Payload = $"{{\"n\":{i}}}",
                SequenceNumber = 11 + i,
                PreviousHash = previous
            };
            e.Hash = Hashes.ComputeEventHash(new EventHashInput
            {
                EventId = e.EventId, EngagementId = e.EngagementId, TenantId = e.TenantId, Actor = e.Actor, Type = e.Type,
                Timestamp = e.Timestamp, Payload = e.Payload, PreviousHash = previous
            });
            previous = e.Hash;
            events.Add(e);
        }
        return events;
    }

    private static ProofOfDeliveryTimelineEvent Timeline(AuditEvent e) => new()
    {
        EventId = e.EventId, EngagementId = e.EngagementId, TenantId = e.TenantId, Actor = e.Actor, Type = e.Type,
        Timestamp = e.Timestamp, Payload = e.Payload, SequenceNumber = e.SequenceNumber, Hash = e.Hash, PreviousHash = e.PreviousHash
    };

    private static ProofOfDeliveryAggregateData Aggregate(IEnumerable<ProofOfDeliveryTimelineEvent> events)
    {
        var list = events.ToList();
        return new ProofOfDeliveryAggregateData
        {
            Engagement = Summary(),
            HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(list, Now),
            Events = list
        };
    }

    private static void AssertVerifiedThrough(ProofOfDeliveryVerificationResult result, IReadOnlyList<AuditEvent> chain, int verified)
    {
        Assert.Equal(verified, result.VerifiedEventCount);
        Assert.Equal(chain[0].SequenceNumber, result.FirstSequenceNumber);
        Assert.Equal(chain[verified - 1].SequenceNumber, result.LastSequenceNumber);
        Assert.Equal(chain[0].EventId, result.FirstEventId);
        Assert.Equal(chain[verified - 1].EventId, result.LastEventId);
        Assert.Equal(chain[0].Timestamp, result.FirstEventTimestamp);
        Assert.Equal(chain[verified - 1].Timestamp, result.LastEventTimestamp);
        Assert.Equal(chain[verified - 1].Hash, result.StoredChainTipHash);
        Assert.Equal(HashChainService.Genesis, result.GenesisHash);
        Assert.Equal(Now, result.VerifiedAtUtc);
    }

    private static void AssertNothingVerified(ProofOfDeliveryVerificationResult result)
    {
        Assert.Equal(0, result.VerifiedEventCount);
        Assert.Null(result.FirstSequenceNumber);
        Assert.Null(result.LastSequenceNumber);
        Assert.Null(result.FirstEventId);
        Assert.Null(result.LastEventId);
        Assert.Null(result.FirstEventTimestamp);
        Assert.Null(result.LastEventTimestamp);
        Assert.Null(result.StoredChainTipHash);
    }

    // ==========================================
    // Where a broken chain stops being verified
    // ==========================================

    [Fact]
    public void BrokenLinkAtTheThirdEvent_VerifiesTheFirstTwoOnly()
    {
        var chain = Chain("Created", "StageChange", "StageChange", "Delivered");
        var events = chain.Select(Timeline).ToList();
        events[2] = Timeline(chain[2]) with { PreviousHash = new string('f', 64) };

        var result = Service.VerifyHistory(Aggregate(events));

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        AssertVerifiedThrough(result, chain, verified: 2);
        Assert.Equal(chain[2].EventId, result.BrokenAtEventId);
        Assert.Equal(13, result.BrokenAtSequenceNumber);
        Assert.Equal("Event at sequence 13 previous_hash does not match the prior event's hash.", result.FailureReason);
        Assert.False(result.IsConsistentWithLiveHead);
        Assert.False(result.HasDeliveryEvidence); // the Delivered event is after the break
    }

    [Fact]
    public void HashMismatchAtTheThirdEvent_VerifiesTheFirstTwoOnly()
    {
        var chain = Chain("Created", "StageChange", "StageChange", "Delivered");
        var events = chain.Select(Timeline).ToList();
        events[2] = Timeline(chain[2]) with { Payload = "{\"n\":999}" };

        var result = Service.VerifyHistory(Aggregate(events));

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        AssertVerifiedThrough(result, chain, verified: 2);
        Assert.Equal(chain[2].EventId, result.BrokenAtEventId);
        Assert.Equal(13, result.BrokenAtSequenceNumber);
        Assert.Equal($"Stored hash does not match recomputed SHA-256 hash at sequence 13 (Event ID '{chain[2].EventId}').", result.FailureReason);
        Assert.False(result.IsConsistentWithLiveHead);
        Assert.False(result.HasDeliveryEvidence);
    }

    [Fact]
    public void HashMismatchOnTheFirstEvent_VerifiesNothing()
    {
        var chain = Chain("Created", "StageChange");
        var events = chain.Select(Timeline).ToList();
        events[0] = Timeline(chain[0]) with { Actor = "intruder" };

        var result = Service.VerifyHistory(Aggregate(events));

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, result.Status);
        AssertNothingVerified(result);
        Assert.Equal(chain[0].EventId, result.BrokenAtEventId);
        Assert.Equal(11, result.BrokenAtSequenceNumber);
    }

    [Fact]
    public void FirstEventNotFromGenesis_VerifiesNothing_WithTheGenesisMessage()
    {
        var chain = Chain("Created");
        var events = new[] { Timeline(chain[0]) with { PreviousHash = new string('1', 64) } };

        var result = Service.VerifyHistory(Aggregate(events));

        AssertNothingVerified(result);
        Assert.Equal("First event previous_hash does not match the genesis hash.", result.FailureReason);
    }

    [Fact]
    public void EventWithNoStoredHash_IsTampered()
    {
        var chain = Chain("Created");
        var events = new[] { Timeline(chain[0]) with { Hash = null } };

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Tampered, Service.VerifyHistory(Aggregate(events)).Status);
    }

    [Fact]
    public void ValidChain_VerifiesEverything_AndPassesTheLiveHeadFlagThrough()
    {
        var chain = Chain("Created", "StageChange", "StageChange");

        var consistent = Service.VerifyHistory(Aggregate(chain.Select(Timeline)), isConsistentWithLiveHead: true);
        var inconsistent = Service.VerifyHistory(Aggregate(chain.Select(Timeline)), isConsistentWithLiveHead: false);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, consistent.Status);
        AssertVerifiedThrough(consistent, chain, verified: 3);
        Assert.True(consistent.IsConsistentWithLiveHead);
        Assert.False(inconsistent.IsConsistentWithLiveHead);
        Assert.Null(consistent.BrokenAtEventId);
        Assert.Null(consistent.FailureReason);
    }

    [Fact]
    public void EmptyChain_PassesTheLiveHeadFlagThrough()
    {
        Assert.False(Service.VerifyHistory(Aggregate([]), isConsistentWithLiveHead: false).IsConsistentWithLiveHead);
        Assert.True(Service.VerifyHistory(Aggregate([]), isConsistentWithLiveHead: true).IsConsistentWithLiveHead);
    }

    [Fact]
    public void NullAggregate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Service.VerifyHistory(null!));
    }

    // ==========================================
    // Delivery evidence
    // ==========================================

    [Theory]
    [InlineData("ProofOfDelivery", true)]
    [InlineData("delivery", true)]
    [InlineData("DELIVERED", true)]
    [InlineData("EngagementClosed", false)]
    [InlineData("DeliveryScheduled", false)]
    public void DeliveryEvidence_NeedsAnExplicitDeliveryEvent(string type, bool expected)
    {
        var chain = Chain("Created", type, "StageChange");

        Assert.Equal(expected, Service.VerifyHistory(Aggregate(chain.Select(Timeline))).HasDeliveryEvidence);
    }

    // ==========================================
    // Live-head consistency
    // ==========================================

    private void StoredChain(IReadOnlyList<AuditEvent> chain) =>
        _repository.Setup(r => r.GetByEngagementIdInChainOrderAsync(Tenant, Engagement)).ReturnsAsync(chain);

    [Fact]
    public async Task LiveHead_MatchingTheSnapshotTip_IsConsistent_WithoutARefresh()
    {
        var chain = Chain("Created", "StageChange");
        StoredChain(chain);
        _repository.Setup(r => r.GetLatestForEngagementAsync(Tenant, Engagement)).ReturnsAsync(chain[^1]);

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.True(data.Verification.IsConsistentWithLiveHead);
        _repository.Verify(r => r.GetByEngagementIdInChainOrderAsync(Tenant, Engagement), Times.Once);
    }

    [Fact]
    public async Task LiveHead_NoEventsAnywhere_IsConsistent()
    {
        StoredChain([]);

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.Equal(ProofOfDeliveryIntegrityStatus.Empty, data.Verification.Status);
        Assert.True(data.Verification.IsConsistentWithLiveHead);
    }

    [Fact]
    public async Task LiveHead_EventsInTheStoreButNoneInTheSnapshot_IsInconsistent()
    {
        StoredChain([]);
        _repository.Setup(r => r.GetLatestForEngagementAsync(Tenant, Engagement)).ReturnsAsync(Chain("Created")[0]);

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.False(data.Verification.IsConsistentWithLiveHead);
        _repository.Verify(r => r.GetByEngagementIdInChainOrderAsync(Tenant, Engagement), Times.Exactly(2)); // refreshed once
    }

    [Fact]
    public async Task LiveHead_SnapshotHasEventsButTheStoreReturnsNone_IsInconsistent()
    {
        StoredChain(Chain("Created"));

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.False(data.Verification.IsConsistentWithLiveHead);
    }

    public static TheoryData<string> TipMismatches => new() { "sequence", "eventId", "hash" };

    [Theory]
    [MemberData(nameof(TipMismatches))]
    public async Task LiveHead_DifferingInAnyOfSequenceIdOrHash_IsInconsistent(string field)
    {
        var chain = Chain("Created", "StageChange");
        StoredChain(chain);
        var tip = chain[^1];
        var live = new AuditEvent
        {
            EventId = field == "eventId" ? Guid.NewGuid() : tip.EventId,
            SequenceNumber = field == "sequence" ? tip.SequenceNumber + 1 : tip.SequenceNumber,
            Hash = field == "hash" ? new string('9', 64) : tip.Hash,
            TenantId = Tenant,
            EngagementId = Engagement
        };
        _repository.Setup(r => r.GetLatestForEngagementAsync(Tenant, Engagement)).ReturnsAsync(live);

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.False(data.Verification.IsConsistentWithLiveHead);
        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, data.Verification.Status); // the snapshot itself still verifies
    }

    [Fact]
    public async Task LiveHead_ThatCannotBeRead_IsTreatedAsInconsistent()
    {
        StoredChain(Chain("Created"));
        _repository.Setup(r => r.GetLatestForEngagementAsync(Tenant, Engagement)).ThrowsAsync(new TimeoutException("db"));

        var data = await Service.GetVerifiedReportDataAsync(Tenant, Engagement);

        Assert.False(data.Verification.IsConsistentWithLiveHead);
        Assert.Equal(ProofOfDeliveryIntegrityStatus.Valid, data.Verification.Status);
    }

    [Fact]
    public async Task Aggregate_CopiesEveryEventFieldIntoTheTimeline()
    {
        var chain = Chain("Created");
        StoredChain(chain);

        var aggregate = await Service.GetAggregateHistoryAsync(Tenant, Engagement);

        var e = Assert.Single(aggregate.Events);
        Assert.Equal(
            (chain[0].EventId, chain[0].EngagementId, chain[0].TenantId, chain[0].Actor, chain[0].Type, chain[0].Timestamp, chain[0].Payload, chain[0].SequenceNumber, chain[0].Hash, chain[0].PreviousHash),
            (e.EventId, e.EngagementId, e.TenantId, e.Actor, e.Type, e.Timestamp, e.Payload, e.SequenceNumber, e.Hash, e.PreviousHash));
        Assert.Equal(chain[0].Hash, aggregate.HistoryBoundary.ChainTipHash);
    }
}
