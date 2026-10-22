using Custodian.Audit.Data;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.HashChain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// AuditEventRepository on the EF in-memory provider: tenant and engagement scoping on every read, the
/// chain-head rules of append and flag, and metadata. The transaction, the SELECT ... FOR UPDATE lock and
/// the MySQL deadlock retry only run against a relational database; Integration/AuditEventRepositoryTests
/// covers those.
/// </summary>
public class AuditEventRepositoryUnitTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtherTenant = Guid.NewGuid();
    private static readonly Guid Engagement = Guid.NewGuid();
    private static readonly Guid OtherEngagement = Guid.NewGuid();

    private readonly AuditDbContext _db = new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseInMemoryDatabase("audit-repo-" + Guid.NewGuid().ToString("N")).Options);

    private AuditEventRepository Repository => new(_db);

    private AuditEvent Event(Guid? tenant = null, Guid? engagement = null, long sequence = 0, DateTime? at = null, string hash = "h") => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = tenant ?? Tenant,
        EngagementId = engagement ?? Engagement,
        Actor = "actor",
        Type = "StatusChange",
        Timestamp = at ?? DateTime.UtcNow,
        SequenceNumber = sequence,
        Hash = hash + sequence
    };

    private AuditEvent Seed(Guid? tenant = null, Guid? engagement = null, long sequence = 0, DateTime? at = null)
    {
        var e = Event(tenant, engagement, sequence, at);
        _db.Events.Add(e);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return e;
    }

    /// <summary>Builds the appended event from the previous hash, as the service does.</summary>
    private static Func<string, AuditEvent> Build(Guid eventId, Guid tenant, Guid engagement, long sequence, string? hash = "new-hash") =>
        previousHash => new AuditEvent
        {
            EventId = eventId,
            TenantId = tenant,
            EngagementId = engagement,
            Actor = "actor",
            Type = "StatusChange",
            SequenceNumber = sequence,
            PreviousHash = previousHash,
            Hash = hash
        };

    // ==========================================
    // Reads
    // ==========================================

    [Fact]
    public async Task AddAsync_SavesTheEvent()
    {
        var e = Event();

        Assert.Same(e, await Repository.AddAsync(e));

        _db.ChangeTracker.Clear();
        Assert.Equal(e.EventId, (await _db.Events.SingleAsync()).EventId);
    }

    [Fact]
    public async Task GetByIdAsync_IsScopedToTheTenant()
    {
        var e = Seed();
        Seed(tenant: OtherTenant);

        Assert.Equal(e.EventId, (await Repository.GetByIdAsync(e.EventId, Tenant))!.EventId);
        Assert.Null(await Repository.GetByIdAsync(e.EventId, OtherTenant));
        Assert.Null(await Repository.GetByIdAsync(Guid.NewGuid(), Tenant));
    }

    [Fact]
    public async Task GetByEngagement_ReturnsOnlyThatEngagementInTheTenant_InSequenceOrder()
    {
        var second = Seed(sequence: 2);
        var first = Seed(sequence: 1);
        Seed(tenant: OtherTenant, sequence: 3);
        Seed(engagement: OtherEngagement, sequence: 4);

        var byEngagement = (await Repository.GetByEngagementIdAsync(Engagement, Tenant)).Select(e => e.EventId);
        var inChainOrder = (await Repository.GetByEngagementIdInChainOrderAsync(Tenant, Engagement)).Select(e => e.EventId);

        Assert.Equal([first.EventId, second.EventId], byEngagement);
        Assert.Equal([first.EventId, second.EventId], inChainOrder);
    }

    [Fact]
    public async Task GetByTenant_ReturnsOnlyTheTenant_NewestFirst()
    {
        var older = Seed(sequence: 1, at: DateTime.UtcNow.AddHours(-2));
        var newer = Seed(engagement: OtherEngagement, sequence: 2, at: DateTime.UtcNow.AddHours(-1));
        Seed(tenant: OtherTenant, sequence: 3);

        Assert.Equal([newer.EventId, older.EventId], (await Repository.GetByTenantIdAsync(Tenant)).Select(e => e.EventId));
    }

    [Fact]
    public async Task GetLatestForEngagement_IsTheHighestSequenceInThatEngagementAndTenant()
    {
        Seed(sequence: 1);
        var latest = Seed(sequence: 5);
        Seed(sequence: 3);
        Seed(tenant: OtherTenant, sequence: 9);
        Seed(engagement: OtherEngagement, sequence: 8);

        Assert.Equal(latest.EventId, (await Repository.GetLatestForEngagementAsync(Tenant, Engagement))!.EventId);
        Assert.Null(await Repository.GetLatestForEngagementAsync(Tenant, Guid.NewGuid()));
    }

    // ==========================================
    // Append
    // ==========================================

    [Fact]
    public async Task Append_FirstEvent_StartsFromGenesis_AndCreatesTheHead()
    {
        var id = Guid.NewGuid();

        var result = await Repository.AppendToChainAsync(Tenant, Engagement, id, Build(id, Tenant, Engagement, 1));

        Assert.True(result.Created);
        Assert.Equal(HashChainService.Genesis, result.Event.PreviousHash);
        var head = await _db.ChainHeads.SingleAsync();
        Assert.Equal((Engagement, Tenant, id, "new-hash"), (head.EngagementId, head.TenantId, head.LastEventId!.Value, head.LastHash));
        Assert.True(head.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Append_WithoutAHead_ContinuesFromTheLatestEventOfThatEngagementAndTenant()
    {
        Seed(sequence: 1);
        var latest = Seed(sequence: 2);
        Seed(tenant: OtherTenant, sequence: 7);
        Seed(engagement: OtherEngagement, sequence: 8);
        var id = Guid.NewGuid();

        var result = await Repository.AppendToChainAsync(Tenant, Engagement, id, Build(id, Tenant, Engagement, 3));

        Assert.Equal(latest.Hash, result.Event.PreviousHash);
    }

    [Fact]
    public async Task Append_ExistingHead_ChainsFromItsHash()
    {
        _db.ChainHeads.Add(new EngagementChainHead { EngagementId = Engagement, TenantId = Tenant, LastHash = "head-hash" });
        _db.SaveChanges();
        var id = Guid.NewGuid();

        var result = await Repository.AppendToChainAsync(Tenant, Engagement, id, Build(id, Tenant, Engagement, 1));

        Assert.Equal("head-hash", result.Event.PreviousHash);
        Assert.Equal("new-hash", (await _db.ChainHeads.SingleAsync()).LastHash);
    }

    [Fact]
    public async Task Append_HeadOfAnotherTenant_IsAConflict()
    {
        _db.ChainHeads.Add(new EngagementChainHead { EngagementId = Engagement, TenantId = OtherTenant, LastHash = "x" });
        _db.SaveChanges();
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            Repository.AppendToChainAsync(Tenant, Engagement, id, Build(id, Tenant, Engagement, 1)));

        Assert.Equal($"Engagement '{Engagement}' belongs to another tenant's audit chain.", error.Message);
        Assert.Empty(_db.Events);
    }

    [Fact]
    public async Task Append_RedeliveredEvent_IsReturnedUnchanged()
    {
        var existing = Seed(sequence: 1);

        var result = await Repository.AppendToChainAsync(Tenant, Engagement, existing.EventId, _ => throw new InvalidOperationException("must not build"));

        Assert.False(result.Created);
        Assert.Equal(existing.EventId, result.Event.EventId);
        Assert.Single(_db.Events);
    }

    [Fact]
    public async Task Append_EventIdUsedByAnotherTenant_IsAConflict()
    {
        var foreign = Seed(tenant: OtherTenant);

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            Repository.AppendToChainAsync(Tenant, Engagement, foreign.EventId, Build(foreign.EventId, Tenant, Engagement, 1)));

        Assert.Equal($"Event '{foreign.EventId}' is already recorded for another tenant.", error.Message);
    }

    [Fact]
    public async Task Append_EventWithoutHash_IsRejected()
    {
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository.AppendToChainAsync(Tenant, Engagement, id, Build(id, Tenant, Engagement, 1, hash: null)));

        Assert.Equal("An appended event must carry its hash.", error.Message);
    }

    // ==========================================
    // Metadata
    // ==========================================

    [Fact]
    public async Task SetMetadata_Guards()
    {
        var e = Seed();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Repository.SetMetadataAsync(null!));
        Assert.StartsWith("EventId is required.", (await Assert.ThrowsAsync<ArgumentException>(() =>
            Repository.SetMetadataAsync(new AuditEventMetadata { TenantId = Tenant }))).Message);
        Assert.StartsWith("TenantId is required.", (await Assert.ThrowsAsync<ArgumentException>(() =>
            Repository.SetMetadataAsync(new AuditEventMetadata { EventId = e.EventId }))).Message);
        var missing = Guid.NewGuid();
        Assert.Equal($"Audit event '{missing}' was not found.", (await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Repository.SetMetadataAsync(new AuditEventMetadata { EventId = missing, TenantId = Tenant }))).Message);
        Assert.Equal($"Audit event '{e.EventId}' belongs to another tenant.", (await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            Repository.SetMetadataAsync(new AuditEventMetadata { EventId = e.EventId, TenantId = OtherTenant }))).Message);
    }

    [Fact]
    public async Task SetMetadata_ExistingRowOfAnotherTenant_IsAConflict()
    {
        var e = Seed();
        _db.EventMetadata.Add(new AuditEventMetadata { EventId = e.EventId, TenantId = OtherTenant });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            Repository.SetMetadataAsync(new AuditEventMetadata { EventId = e.EventId, TenantId = Tenant }));

        Assert.Equal($"Metadata for event '{e.EventId}' belongs to another tenant.", error.Message);
    }

    [Fact]
    public async Task SetMetadata_UpdatesTheExistingRow_AndSavesIt()
    {
        var e = Seed();
        _db.EventMetadata.Add(new AuditEventMetadata { EventId = e.EventId, TenantId = Tenant });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        await Repository.SetMetadataAsync(new AuditEventMetadata { EventId = e.EventId, TenantId = Tenant, IsArchived = true, ArchiveReason = "old" });

        _db.ChangeTracker.Clear();
        var stored = await _db.EventMetadata.SingleAsync();
        Assert.True(stored.IsArchived);
        Assert.Equal("old", stored.ArchiveReason);
    }

    [Fact]
    public async Task GetMetadata_IsScopedToTheTenant()
    {
        var e = Seed();
        var other = Seed(tenant: OtherTenant);
        _db.EventMetadata.AddRange(
            new AuditEventMetadata { EventId = e.EventId, TenantId = Tenant },
            new AuditEventMetadata { EventId = other.EventId, TenantId = OtherTenant });
        _db.SaveChanges();

        Assert.NotNull(await Repository.GetMetadataAsync(e.EventId, Tenant));
        Assert.Null(await Repository.GetMetadataAsync(e.EventId, OtherTenant));
        Assert.Null(await Repository.GetMetadataAsync(other.EventId, Tenant));
    }

    [Fact]
    public async Task GetMetadataForEvents_NoIds_IsEmpty()
    {
        Assert.Empty(await Repository.GetMetadataForEventsAsync([], Tenant));
    }

    // ==========================================
    // Flag
    // ==========================================

    private static Func<string, AuditEvent, AuditEvent> BuildFlag(long sequence = 0, string? hash = "flag-hash") =>
        (previousHash, original) => new AuditEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = original.TenantId,
            EngagementId = original.EngagementId,
            Actor = "staff",
            Type = "AuditEventFlagged",
            SequenceNumber = sequence,
            PreviousHash = previousHash,
            Hash = hash
        };

    [Fact]
    public async Task Flag_WithoutAHead_ChainsFromTheLatestEventOfThatEngagementAndTenant_AndNumbersTheEvent()
    {
        var original = Seed(sequence: 1);
        var latest = Seed(sequence: 4);
        Seed(tenant: OtherTenant, sequence: 6);
        Seed(engagement: OtherEngagement, sequence: 5);

        var result = await Repository.FlagEventAsync(Tenant, original.EventId, "Wrong amount", "staff-1", BuildFlag());

        Assert.NotNull(result);
        Assert.True(result.Created);
        Assert.Equal(latest.Hash, result.ReferenceEvent!.PreviousHash);
        Assert.Equal(7, result.ReferenceEvent.SequenceNumber); // next after the highest in the store
        var head = await _db.ChainHeads.SingleAsync();
        Assert.Equal((Engagement, Tenant, "flag-hash", result.ReferenceEvent.EventId), (head.EngagementId, head.TenantId, head.LastHash, head.LastEventId!.Value));
        var meta = await _db.EventMetadata.SingleAsync();
        Assert.Equal((true, "Wrong amount", "staff-1", result.ReferenceEvent.EventId, false),
            (meta.IsFlagged, meta.FlagReason, meta.FlaggedBy, meta.FlagReferenceEventId!.Value, meta.IsArchived));
    }

    [Fact]
    public async Task Flag_OnlyEventOfAnEngagement_StartsAHeadFromItsHash()
    {
        var original = Seed(sequence: 1);

        var result = await Repository.FlagEventAsync(Tenant, original.EventId, "r", "s", BuildFlag(sequence: 42));

        Assert.Equal(original.Hash, result!.ReferenceEvent!.PreviousHash);
        Assert.Equal(42, result.ReferenceEvent.SequenceNumber); // a sequence the builder set is kept
    }

    [Fact]
    public async Task Flag_EngagementWithNoHashYet_StartsFromGenesis()
    {
        var original = Event(sequence: 1);
        original.Hash = null;
        _db.Events.Add(original);
        _db.SaveChanges();

        var result = await Repository.FlagEventAsync(Tenant, original.EventId, "r", "s", BuildFlag());

        Assert.Equal(HashChainService.Genesis, result!.ReferenceEvent!.PreviousHash);
    }

    [Fact]
    public async Task Flag_HeadOfAnotherTenant_IsAConflict()
    {
        var original = Seed(sequence: 1);
        _db.ChainHeads.Add(new EngagementChainHead { EngagementId = Engagement, TenantId = OtherTenant, LastHash = "x" });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() => Repository.FlagEventAsync(Tenant, original.EventId, "r", "s", BuildFlag()));

        Assert.Equal($"Engagement '{Engagement}' belongs to another tenant's audit chain.", error.Message);
    }

    [Fact]
    public async Task Flag_UnknownEvent_IsNull_AndAnotherTenantsEvent_IsAConflict()
    {
        var foreign = Seed(tenant: OtherTenant);

        Assert.Null(await Repository.FlagEventAsync(Tenant, Guid.NewGuid(), "r", "s", BuildFlag()));
        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() => Repository.FlagEventAsync(Tenant, foreign.EventId, "r", "s", BuildFlag()));
        Assert.Equal($"Audit event '{foreign.EventId}' belongs to another tenant.", error.Message);
        Assert.Single(_db.Events);
    }

    [Fact]
    public async Task Flag_AlreadyFlagged_ReturnsTheExistingFlag_WithoutAReferenceEvent()
    {
        var original = Seed(sequence: 1);
        await Repository.FlagEventAsync(Tenant, original.EventId, "first", "s", BuildFlag());

        var again = await Repository.FlagEventAsync(Tenant, original.EventId, "second", "s", BuildFlag());

        Assert.False(again!.Created);
        Assert.Null(again.ReferenceEvent);
        Assert.Equal("first", again.Metadata.FlagReason);
        Assert.Equal(2, await _db.Events.CountAsync());
    }

    [Fact]
    public async Task Flag_ExistingMetadataOfAnotherTenant_IsAConflict()
    {
        var original = Seed(sequence: 1);
        _db.EventMetadata.Add(new AuditEventMetadata { EventId = original.EventId, TenantId = OtherTenant, IsFlagged = true });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() => Repository.FlagEventAsync(Tenant, original.EventId, "r", "s", BuildFlag()));

        Assert.Equal($"Metadata for event '{original.EventId}' belongs to another tenant.", error.Message);
    }

    [Fact]
    public async Task Flag_ArchivedEvent_KeepsItsArchiveAndAddsTheFlag()
    {
        var original = Seed(sequence: 1);
        _db.EventMetadata.Add(new AuditEventMetadata { EventId = original.EventId, TenantId = Tenant, IsArchived = true, ArchiveReason = "old" });
        _db.SaveChanges();

        var result = await Repository.FlagEventAsync(Tenant, original.EventId, "Wrong", "staff-1", BuildFlag());

        _db.ChangeTracker.Clear();
        var meta = await _db.EventMetadata.SingleAsync();
        Assert.Equal((true, true, "old", "Wrong", "staff-1", result!.ReferenceEvent!.EventId),
            (meta.IsFlagged, meta.IsArchived, meta.ArchiveReason, meta.FlagReason, meta.FlaggedBy, meta.FlagReferenceEventId!.Value));
        Assert.NotNull(meta.FlaggedAt);
    }

    [Fact]
    public async Task Flag_ReferenceEventWithoutHash_IsRejected()
    {
        var original = Seed(sequence: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.FlagEventAsync(Tenant, original.EventId, "r", "s", BuildFlag(hash: null)));

        Assert.Equal("An appended event must carry its hash.", error.Message);
    }

    // ==========================================
    // Archive
    // ==========================================

    [Fact]
    public async Task Archive_NewMetadata_IsArchivedButNotFlagged()
    {
        var original = Seed(sequence: 1);

        var result = await Repository.ArchiveEventAsync(Tenant, original.EventId, "Superseded", "staff-1");

        Assert.True(result!.Created);
        _db.ChangeTracker.Clear();
        var meta = await _db.EventMetadata.SingleAsync();
        Assert.Equal((true, false, "Superseded", "staff-1"), (meta.IsArchived, meta.IsFlagged, meta.ArchiveReason, meta.ArchivedBy));
        Assert.NotNull(meta.ArchivedAt);
        Assert.Single(_db.Events); // archiving never appends to the chain
    }

    [Fact]
    public async Task Archive_AlreadyArchived_IsNotCreatedAgain()
    {
        var original = Seed(sequence: 1);
        await Repository.ArchiveEventAsync(Tenant, original.EventId, "first", "s");

        var again = await Repository.ArchiveEventAsync(Tenant, original.EventId, "second", "s");

        Assert.False(again!.Created);
        Assert.Equal("first", again.Metadata.ArchiveReason);
    }

    [Fact]
    public async Task Archive_ExistingMetadataOfAnotherTenant_IsAConflict()
    {
        var original = Seed(sequence: 1);
        _db.EventMetadata.Add(new AuditEventMetadata { EventId = original.EventId, TenantId = OtherTenant, IsArchived = true });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() => Repository.ArchiveEventAsync(Tenant, original.EventId, "r", "s"));

        Assert.Equal($"Metadata for event '{original.EventId}' belongs to another tenant.", error.Message);
    }

    [Fact]
    public async Task Archive_UnknownEvent_IsNull_AndAnotherTenantsEvent_IsAConflict()
    {
        var foreign = Seed(tenant: OtherTenant);

        Assert.Null(await Repository.ArchiveEventAsync(Tenant, Guid.NewGuid(), "r", "s"));
        var error = await Assert.ThrowsAsync<AuditChainConflictException>(() => Repository.ArchiveEventAsync(Tenant, foreign.EventId, "r", "s"));
        Assert.Equal($"Audit event '{foreign.EventId}' belongs to another tenant.", error.Message);
    }
}
