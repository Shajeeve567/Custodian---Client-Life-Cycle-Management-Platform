using System.Text.Json;
using Confluent.Kafka;
using Custodian.Shared.Messaging;
using Custodian.Workflow.Models;
using Microsoft.Extensions.Options;

namespace Custodian.Workflow.Services.Kafka;

/// <summary>
/// Kafka-based implementation of IAuditPublisher. Publishes engagement lifecycle
/// events (Genesis, StatusChange, StageChange) onto the shared "custodian.events"
/// topic, wrapped in the shared KafkaEnvelope contract. Selected instead of the
/// HTTP-based AuditPublisher via the "Audit:Transport" config switch (see Program.cs).
///
/// IProducer&lt;TKey,TValue&gt; is thread-safe and expensive to construct, so it's
/// registered once as a singleton and injected here rather than built per-call.
/// </summary>
public class KafkaAuditPublisher : IAuditPublisher
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaProducerOptions _options;
    private readonly ILogger<KafkaAuditPublisher> _logger;

    public KafkaAuditPublisher(
        IProducer<string, string> producer,
        IOptions<KafkaProducerOptions> options,
        ILogger<KafkaAuditPublisher> logger)
    {
        _producer = producer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishEventAsync(Guid engagementId, string tenantId, string actor, string type, object payload)
    {
        try
        {
            var enrichedPayload = new EngagementEventPayload(
                EngagementId: engagementId,
                Actor: actor,
                Data: JsonSerializer.SerializeToElement(payload));

            var envelope = KafkaEnvelope.Create(type, tenantId, enrichedPayload);
            var json = JsonSerializer.Serialize(envelope);

            // Key by engagementId so every event for the same engagement lands on the
            // same partition, and is therefore read back in the order it was produced.
            // Kafka only guarantees ordering within a partition, not across a topic.
            var result = await _producer.ProduceAsync(
                _options.Topic,
                new Message<string, string> { Key = engagementId.ToString(), Value = json });

            _logger.LogInformation(
                "Published '{Type}' event for engagement '{EngagementId}' to {Topic} [partition={Partition}, offset={Offset}]",
                type, engagementId, _options.Topic, result.Partition.Value, result.Offset.Value);
        }
        catch (Exception ex)
        {
            // Per business rules: audit event side effects must not silently corrupt/fail the primary business transaction
            _logger.LogError(ex, "Error publishing Kafka audit event '{Type}' for engagement '{EngagementId}'", type, engagementId);
        }
    }

    public async Task PublishGenesisEventAsync(Engagement engagement, string tenantId)
    {
        await PublishEventAsync(
            engagement.EngagementId,
            tenantId,
            "System",
            "Genesis",
            new
            {
                status = engagement.Status.ToString(),
                stage = engagement.Stage.ToString(),
                clientId = engagement.ClientId,
                staffId = engagement.StaffId,
                createdAt = engagement.CreatedAt
            });
    }
}
