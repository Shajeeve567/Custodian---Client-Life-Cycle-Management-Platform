using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.HashChain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// CSTD-40 (H4): appends go through the engagement's chain head. EF InMemory runs the same logic
/// without the transaction and row lock; the lock itself needs MySQL (see AuditEventRepository).
/// </summary>
public class ChainAppendTests
{
    private const string Genesis = HashChainService.Genesis;
    private readonly Guid _tenant = Guid.NewGuid();

    private static AuditDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<AuditDbContext>().UseInMemoryDatabase(name).Options);

    private static Func<string, AuditEvent> Builder(Guid tenant, Guid engagement, Guid eventId, string hash) => previous => new AuditEvent
    {
        EventId = eventId,
        EngagementId = engagement,
        TenantId = tenant,
        Actor = "System",
        Type = "StageChange",
        Payload = "{}",
        Hash = hash,
        PreviousHash = previous
    };

    [Fact]
    public async Task EachEngagementChainsFromGenesis_AndLinksToItsOwnLastHash()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var repo = new AuditEventRepository(db);
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();

        var a1 = await repo.AppendToChainAsync(_tenant, engagementA, Guid.NewGuid(), Builder(_tenant, engagementA, Guid.NewGuid(), new string('1', 64)));
        var b1 = await repo.AppendToChainAsync(_tenant, engagementB, Guid.NewGuid(), Builder(_tenant, engagementB, Guid.NewGuid(), new string('2', 64)));
        var a2 = await repo.AppendToChainAsync(_tenant, engagementA, Guid.NewGuid(), Builder(_tenant, engagementA, Guid.NewGuid(), new string('3', 64)));

        Assert.Equal(Genesis, a1.Event.PreviousHash);
        Assert.Equal(Genesis, b1.Event.PreviousHash);
        Assert.Equal(new string('1', 64), a2.Event.PreviousHash);

        var headA = await db.ChainHeads.SingleAsync(h => h.EngagementId == engagementA);
        Assert.Equal(new string('3', 64), headA.LastHash);
        Assert.Equal(a2.Event.EventId, headA.LastEventId);
    }

    [Fact]
    public async Task RedeliveredEventId_ReturnsExistingRow_WithoutBuildingOrMovingTheHead()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var repo = new AuditEventRepository(db);
        var engagement = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await repo.AppendToChainAsync(_tenant, engagement, eventId, Builder(_tenant, engagement, eventId, new string('1', 64)));

        var builderCalled = false;
        var again = await repo.AppendToChainAsync(_tenant, engagement, eventId, previous =>
        {
            builderCalled = true;
            return Builder(_tenant, engagement, eventId, new string('9', 64))(previous);
        });

        Assert.False(again.Created);
        Assert.False(builderCalled);
        Assert.Single(db.Events);
        Assert.Equal(new string('1', 64), (await db.ChainHeads.SingleAsync()).LastHash);
    }

    [Fact]
    public async Task EventIdOfAnotherTenant_IsRejected()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var repo = new AuditEventRepository(db);
        var engagement = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await repo.AppendToChainAsync(_tenant, engagement, eventId, Builder(_tenant, engagement, eventId, new string('1', 64)));

        var otherTenant = Guid.NewGuid();
        await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            repo.AppendToChainAsync(otherTenant, engagement, eventId, Builder(otherTenant, engagement, eventId, new string('2', 64))));
    }

    [Fact]
    public async Task EngagementChainOfAnotherTenant_IsRejected()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var repo = new AuditEventRepository(db);
        var engagement = Guid.NewGuid();
        await repo.AppendToChainAsync(_tenant, engagement, Guid.NewGuid(), Builder(_tenant, engagement, Guid.NewGuid(), new string('1', 64)));

        var otherTenant = Guid.NewGuid();
        await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            repo.AppendToChainAsync(otherTenant, engagement, Guid.NewGuid(), Builder(otherTenant, engagement, Guid.NewGuid(), new string('2', 64))));
    }

    [Fact]
    public async Task EngagementWithEventsButNoHead_ContinuesFromItsLatestEvent()
    {
        // Events recorded before engagement_chain_heads existed: the first new append must link to them.
        var name = Guid.NewGuid().ToString();
        var engagement = Guid.NewGuid();
        using (var seed = NewContext(name))
        {
            seed.Events.Add(new AuditEvent { EventId = Guid.NewGuid(), EngagementId = engagement, TenantId = _tenant, Actor = "System", Type = "Genesis", SequenceNumber = 1, Hash = new string('a', 64), PreviousHash = Genesis });
            seed.Events.Add(new AuditEvent { EventId = Guid.NewGuid(), EngagementId = engagement, TenantId = _tenant, Actor = "System", Type = "StageChange", SequenceNumber = 2, Hash = new string('b', 64), PreviousHash = new string('a', 64) });
            await seed.SaveChangesAsync();
        }

        using var db = NewContext(name);
        var appended = await new AuditEventRepository(db)
            .AppendToChainAsync(_tenant, engagement, Guid.NewGuid(), Builder(_tenant, engagement, Guid.NewGuid(), new string('c', 64)));

        Assert.Equal(new string('b', 64), appended.Event.PreviousHash);
    }

    [Fact]
    public async Task RecordedChains_VerifyAsIntact_AndATamperedPayloadIsDetected()
    {
        var name = Guid.NewGuid().ToString();
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        using (var db = NewContext(name))
        {
            var service = new AuditEventService(new AuditEventRepository(db), new HashChainService());
            for (var i = 0; i < 10; i++)
            {
                await service.RecordEventAsync(new CreateAuditEventRequest
                {
                    EngagementId = i % 2 == 0 ? engagementA : engagementB,
                    Actor = "System",
                    Type = "ConditionAttached",
                    // Decimal formatting that MySQL's old json column would have rewritten (100.50 -> 100.5).
                    Payload = $"{{\"amount\":100.50,\"step\":{i}}}"
                }, _tenant);
            }

            Assert.True((await service.VerifyChainAsync(_tenant, engagementA)).IsVerified);
            Assert.True((await service.VerifyChainAsync(_tenant, engagementB)).IsVerified);

            var victim = await db.Events.Where(e => e.EngagementId == engagementA).OrderBy(e => e.SequenceNumber).Skip(2).FirstAsync();
            victim.Payload = "{\"amount\":999.00,\"step\":4}";
            await db.SaveChangesAsync();

            var result = await service.VerifyChainAsync(_tenant, engagementA);
            Assert.False(result.IsVerified);
            Assert.Equal(victim.EventId, result.BrokenAtEventId);
            Assert.True((await service.VerifyChainAsync(_tenant, engagementB)).IsVerified);
        }
    }
}
