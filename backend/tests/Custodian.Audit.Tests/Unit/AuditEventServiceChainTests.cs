using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.HashChain;
using Custodian.Audit.Services.Kafka;
using Custodian.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// AuditEventService against a real repository (EF in-memory) and the real hash chain: input validation,
/// chain verification after tampering, flag and archive rules. Also the Kafka consumer's event-type list
/// and its tenant-id mapping.
/// </summary>
public class AuditEventServiceChainTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Engagement = Guid.NewGuid();

    private readonly AuditDbContext _db = new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseInMemoryDatabase("audit-svc-" + Guid.NewGuid().ToString("N")).Options);

    private AuditEventService Service => new(new AuditEventRepository(_db), new HashChainService());

    private static CreateAuditEventRequest Request(string type = "StatusChange", string payload = "{\"to\":\"Started\"}") => new()
    {
        EngagementId = Engagement,
        Actor = "staff-1",
        Type = type,
        Payload = payload
    };

    private async Task<List<AuditEventResponse>> RecordChainAsync(int count)
    {
        var recorded = new List<AuditEventResponse>();
        for (var i = 0; i < count; i++)
        {
            recorded.Add(await Service.RecordEventAsync(Request(payload: $"{{\"n\":{i}}}"), Tenant));
        }
        return recorded;
    }

    // ==========================================
    // RecordEventAsync validation
    // ==========================================

    [Fact]
    public async Task Record_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Service.RecordEventAsync(null!, Tenant));
    }

    [Fact]
    public async Task Record_RequiresEngagementAndType_AndJsonPayload()
    {
        var noEngagement = Request();
        noEngagement.EngagementId = Guid.Empty;

        Assert.StartsWith("EngagementId is required.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.RecordEventAsync(noEngagement, Tenant))).Message);
        Assert.StartsWith("Event Type is required.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.RecordEventAsync(Request(type: " "), Tenant))).Message);
        Assert.StartsWith("Payload must be a valid JSON string.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.RecordEventAsync(Request(payload: "{not json"), Tenant))).Message);
        Assert.Empty(_db.Events);
    }

    [Fact]
    public async Task Record_TimestampIsTruncatedToMicroseconds_SoTheHashSurvivesAMySqlRoundTrip()
    {
        var recorded = await Service.RecordEventAsync(Request(), Tenant);

        Assert.Equal(0, recorded.Timestamp.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.True(recorded.Timestamp <= DateTime.UtcNow);
    }

    // ==========================================
    // VerifyChainAsync
    // ==========================================

    [Fact]
    public async Task Verify_NoEvents_IsVerifiedWithCountZero()
    {
        var result = await Service.VerifyChainAsync(Tenant, Engagement);

        Assert.True(result.IsVerified);
        Assert.Equal(0, result.Count);
        Assert.Equal(Engagement, result.EngagementId);
    }

    [Fact]
    public async Task Verify_UntouchedChain_IsVerified()
    {
        await RecordChainAsync(3);

        var result = await Service.VerifyChainAsync(Tenant, Engagement);

        Assert.True(result.IsVerified);
        Assert.Equal(3, result.Count);
        Assert.Null(result.BrokenAtEventId);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Verify_EditedPayload_IsCaughtAtThatEvent()
    {
        var chain = await RecordChainAsync(3);
        var tampered = await _db.Events.SingleAsync(e => e.EventId == chain[1].EventId);
        tampered.Payload = "{\"n\":42}";
        await _db.SaveChangesAsync();

        var result = await Service.VerifyChainAsync(Tenant, Engagement);

        Assert.False(result.IsVerified);
        Assert.Equal(3, result.Count);
        Assert.Equal(chain[1].EventId, result.BrokenAtEventId);
        Assert.Equal("stored hash does not match recomputed hash", result.Reason);
    }

    [Fact]
    public async Task Verify_BrokenLink_IsCaughtAtThatEvent()
    {
        var chain = await RecordChainAsync(3);
        var tampered = await _db.Events.SingleAsync(e => e.EventId == chain[2].EventId);
        tampered.PreviousHash = new string('f', 64);
        await _db.SaveChangesAsync();

        var result = await Service.VerifyChainAsync(Tenant, Engagement);

        Assert.False(result.IsVerified);
        Assert.Equal(chain[2].EventId, result.BrokenAtEventId);
        Assert.Equal("previous_hash does not match the prior event's hash", result.Reason);
    }

    [Fact]
    public async Task Verify_RemovedHash_IsCaught()
    {
        var chain = await RecordChainAsync(1);
        var tampered = await _db.Events.SingleAsync();
        tampered.Hash = null;
        await _db.SaveChangesAsync();

        var result = await Service.VerifyChainAsync(Tenant, Engagement);

        Assert.False(result.IsVerified);
        Assert.Equal(chain[0].EventId, result.BrokenAtEventId);
    }

    // ==========================================
    // Flag and archive
    // ==========================================

    [Fact]
    public async Task Flag_Validation()
    {
        Assert.StartsWith("EventId is required.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.FlagEventAsync(Guid.Empty, Tenant, "r", "a"))).Message);
        Assert.StartsWith("Reason is required.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.FlagEventAsync(Guid.NewGuid(), Tenant, " ", "a"))).Message);
        Assert.StartsWith("Reason must not exceed 500 characters.",
            (await Assert.ThrowsAsync<ArgumentException>(() => Service.FlagEventAsync(Guid.NewGuid(), Tenant, new string('r', 501), "a"))).Message);
    }

    [Fact]
    public async Task Flag_UnknownEvent_IsNull_AndA500CharReasonIsAccepted()
    {
        Assert.Null(await Service.FlagEventAsync(Guid.NewGuid(), Tenant, new string('r', 500), "a"));
    }

    [Fact]
    public async Task Flag_AppendsAReferenceEvent_DescribingTheFlaggedEvent()
    {
        var chain = await RecordChainAsync(2);

        var flagged = await Service.FlagEventAsync(chain[0].EventId, Tenant, "  Wrong amount ", "staff-9");

        Assert.True(flagged!.IsFlagged);
        Assert.Equal("  Wrong amount ", flagged.FlagReason);
        var reference = await _db.Events.SingleAsync(e => e.Type == "AuditEventFlagged");
        Assert.Equal(("staff-9", Engagement, Tenant, chain[1].Hash), (reference.Actor, reference.EngagementId, reference.TenantId, reference.PreviousHash));
        Assert.Equal(0, reference.Timestamp.Ticks % TimeSpan.TicksPerMicrosecond);
        using var payload = JsonDocument.Parse(reference.Payload);
        Assert.Equal(chain[0].EventId.ToString(), payload.RootElement.GetProperty("referencedEventId").GetString());
        Assert.Equal("StatusChange", payload.RootElement.GetProperty("referencedEventType").GetString());
        Assert.Equal("Wrong amount", payload.RootElement.GetProperty("reason").GetString());
        Assert.True((await Service.VerifyChainAsync(Tenant, Engagement)).IsVerified); // the reference event is properly chained
    }

    [Fact]
    public async Task Archive_Validation_AndUnknownEvent()
    {
        Assert.StartsWith("EventId is required.", (await Assert.ThrowsAsync<ArgumentException>(() => Service.ArchiveEventAsync(Guid.Empty, Tenant, null, "a"))).Message);
        Assert.StartsWith("Reason must not exceed 500 characters.",
            (await Assert.ThrowsAsync<ArgumentException>(() => Service.ArchiveEventAsync(Guid.NewGuid(), Tenant, new string('r', 501), "a"))).Message);
        Assert.Null(await Service.ArchiveEventAsync(Guid.NewGuid(), Tenant, new string('r', 500), "a"));
        Assert.Null(await Service.ArchiveEventAsync(Guid.NewGuid(), Tenant, null, "a"));
    }

    [Fact]
    public async Task Archive_TrimsTheReason_AndReportsIt()
    {
        var chain = await RecordChainAsync(1);

        var archived = await Service.ArchiveEventAsync(chain[0].EventId, Tenant, "  Superseded ", "staff-9");

        Assert.True(archived!.IsArchived);
        Assert.Equal("Superseded", archived.ArchiveReason);
        Assert.Equal("staff-9", archived.ArchivedBy);
        Assert.NotNull(archived.ArchivedAt);
        Assert.False(archived.IsFlagged);
    }

    [Fact]
    public async Task Reads_IncludeFlagAndArchiveMetadata()
    {
        var chain = await RecordChainAsync(2);
        await Service.FlagEventAsync(chain[0].EventId, Tenant, "Wrong", "staff-1");
        await Service.ArchiveEventAsync(chain[1].EventId, Tenant, "Old", "staff-2");

        var byId = await Service.GetEventByIdAsync(chain[0].EventId, Tenant);
        var byEngagement = (await Service.GetEventsByEngagementAsync(Engagement, Tenant)).ToDictionary(e => e.EventId);
        var byTenant = (await Service.GetEventsByTenantAsync(Tenant)).ToDictionary(e => e.EventId);

        Assert.Equal(("Wrong", "staff-1"), (byId!.FlagReason, byId.FlaggedBy));
        Assert.NotNull(byId.FlagReferenceEventId);
        Assert.NotNull(byId.FlaggedAt);
        Assert.True(byEngagement[chain[0].EventId].IsFlagged);
        Assert.Equal("Old", byEngagement[chain[1].EventId].ArchiveReason);
        Assert.True(byTenant[chain[1].EventId].IsArchived);
        Assert.False(byTenant[chain[1].EventId].IsFlagged);
        Assert.Null(await Service.GetEventByIdAsync(chain[0].EventId, Guid.NewGuid()));
    }

    // ==========================================
    // Kafka consumer: event types and tenant ids
    // ==========================================

    private ServiceProvider ConsumerServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IHashChainService, HashChainService>();
        services.AddScoped<IAuditEventService, AuditEventService>();
        return services.BuildServiceProvider();
    }

    private static string Envelope(string eventType, string tenantId) =>
        JsonSerializer.Serialize(KafkaEnvelope.Create(eventType, tenantId,
            new EngagementEventPayload(Engagement, "staff-1", JsonSerializer.SerializeToElement(new { ok = true }))));

    private async Task ConsumeAsync(string eventType, string tenantId)
    {
        using var services = ConsumerServices();
        var consumer = new KafkaAuditEventConsumer(Options.Create(new KafkaOptions()),
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<KafkaAuditEventConsumer>.Instance);
        Assert.True(await consumer.HandleAsync(Envelope(eventType, tenantId)));
    }

    [Theory]
    [InlineData("Genesis")]
    [InlineData("StatusChange")]
    [InlineData("ClientActionStatusChanged")]
    [InlineData("statuschange")]
    public async Task Consumer_RecordsWorkflowLifecycleEvents(string eventType)
    {
        await ConsumeAsync(eventType, Tenant.ToString());

        Assert.Equal(eventType, (await _db.Events.SingleAsync()).Type);
    }

    [Fact]
    public async Task Consumer_GuidTenant_IsUsedAsIs()
    {
        await ConsumeAsync("StatusChange", Tenant.ToString());

        Assert.Equal(Tenant, (await _db.Events.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Consumer_NonGuidTenant_IsMappedToAStableGuidFromItsSha256()
    {
        await ConsumeAsync("StatusChange", "tenant-001");

        var expected = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("tenant-001"))[..16]);
        Assert.Equal(expected, (await _db.Events.SingleAsync()).TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Consumer_BlankTenant_IsTheEmptyTenant(string tenant)
    {
        await ConsumeAsync("StatusChange", tenant);

        Assert.Equal(Guid.Empty, (await _db.Events.SingleAsync()).TenantId);
    }
}
