using System.Text.Json;
using Custodian.Audit.Data;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.Kafka;
using Custodian.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// Tests for KafkaAuditEventConsumer. Mirrors Identity.Tests' KafkaEventEndToEndSimulationTests
/// pattern: hand-build a KafkaEnvelope JSON as if it arrived from Kafka, and call
/// ProcessMessageAsync directly against an EF Core in-memory AuditDbContext — no real broker needed.
/// </summary>
public class KafkaAuditEventConsumerTests
{
    private static ServiceProvider BuildServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IAuditEventService, AuditEventService>();
        return services.BuildServiceProvider();
    }

    private static KafkaAuditEventConsumer CreateConsumer(IServiceProvider provider)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new KafkaAuditEventConsumer(
            Options.Create(new KafkaOptions()),
            scopeFactory,
            NullLogger<KafkaAuditEventConsumer>.Instance);
    }

    private static string BuildEnvelopeJson(string eventType, Guid engagementId, string actor, object data, string? eventId = null, string tenantId = "tenant-001")
    {
        var payload = new EngagementEventPayload(engagementId, actor, JsonSerializer.SerializeToElement(data));
        var envelope = eventId == null
            ? KafkaEnvelope.Create(eventType, tenantId, payload)
            : new KafkaEnvelope(eventId, eventType, tenantId, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload));

        return JsonSerializer.Serialize(envelope);
    }

    [Fact]
    public async Task ProcessMessageAsync_StageChangeEvent_RecordsAuditEvent()
    {
        // Arrange
        using var provider = BuildServices(Guid.NewGuid().ToString());
        var consumer = CreateConsumer(provider);
        var engagementId = Guid.NewGuid();

        var json = BuildEnvelopeJson("StageChange", engagementId, "System", new
        {
            fromStage = "Onboarding",
            toStage = "DocumentCollection",
            changedAt = DateTimeOffset.UtcNow
        });

        // Act
        await consumer.ProcessMessageAsync(json);

        // Assert
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var stored = await db.Events.SingleOrDefaultAsync(e => e.EngagementId == engagementId);

        Assert.NotNull(stored);
        Assert.Equal("StageChange", stored.Type);
        Assert.Equal("System", stored.Actor);
        Assert.Contains("DocumentCollection", stored.Payload);
    }

    [Fact]
    public async Task ProcessMessageAsync_UnhandledEventType_IsIgnored()
    {
        // Arrange: an event type this consumer doesn't care about (e.g. Identity's notification stream)
        using var provider = BuildServices(Guid.NewGuid().ToString());
        var consumer = CreateConsumer(provider);
        var engagementId = Guid.NewGuid();

        var json = BuildEnvelopeJson("document.verified", engagementId, "System", new { documentName = "id.pdf" });

        // Act
        await consumer.ProcessMessageAsync(json);

        // Assert: nothing recorded — this consumer only handles Genesis/StatusChange/StageChange
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.Empty(db.Events);
    }

    [Fact]
    public async Task ProcessMessageAsync_RedeliveredMessage_DoesNotCreateDuplicateRow()
    {
        // Arrange: the single most important guarantee — Kafka delivers at-least-once,
        // so the same message can be processed twice (e.g. consumer restart before commit).
        using var provider = BuildServices(Guid.NewGuid().ToString());
        var consumer = CreateConsumer(provider);
        var engagementId = Guid.NewGuid();
        var eventId = Guid.NewGuid().ToString("N");

        var json = BuildEnvelopeJson("StageChange", engagementId, "System", new
        {
            fromStage = "Onboarding",
            toStage = "DocumentCollection"
        }, eventId: eventId);

        // Act: process the identical message twice
        await consumer.ProcessMessageAsync(json);
        await consumer.ProcessMessageAsync(json);

        // Assert: exactly one row, not two
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var matching = await db.Events.Where(e => e.EngagementId == engagementId).ToListAsync();
        Assert.Single(matching);
    }

    [Fact]
    public async Task ProcessMessageAsync_UnparseableJson_DoesNotThrow()
    {
        // Arrange
        using var provider = BuildServices(Guid.NewGuid().ToString());
        var consumer = CreateConsumer(provider);

        // Act & Assert: malformed messages are logged and discarded, not thrown
        await consumer.ProcessMessageAsync("not valid json at all");
    }
}
