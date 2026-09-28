using System.Text.Json;
using Confluent.Kafka;
using Custodian.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace Custodian.Documents.Services.Kafka;

/// <summary>
/// Publishes document events (document.verified, document.verification_rejected, ...) onto the shared
/// "custodian.events" topic in the same shape Workflow uses: a KafkaEnvelope whose payload is an
/// EngagementEventPayload {EngagementId, Actor, Data}. Three consumers read them:
///  - Audit records them in the engagement's hash chain;
///  - Workflow's DocumentEventsConsumer applies verification outcomes to the linked ClientAction;
///  - Identity's notification consumer tells the client about verification results.
/// Selected with Audit:Transport=Kafka (see Program.cs); the HTTP AuditPublisher remains the fallback.
/// </summary>
public sealed class KafkaAuditPublisher : IAuditPublisher
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

            // Keyed by engagement, like Workflow, so an engagement's events stay ordered on one partition
            // (Audit's hash chain depends on that order).
            var result = await _producer.ProduceAsync(
                _options.Topic,
                new Message<string, string> { Key = engagementId.ToString(), Value = JsonSerializer.Serialize(envelope) });

            _logger.LogInformation(
                "Published '{Type}' event for engagement '{EngagementId}' to {Topic} [partition={Partition}, offset={Offset}]",
                type, engagementId, _options.Topic, result.Partition.Value, result.Offset.Value);
        }
        catch (Exception ex)
        {
            // Audit side effects must not fail the primary document operation.
            _logger.LogError(ex, "Error publishing Kafka event '{Type}' for engagement '{EngagementId}'", type, engagementId);
        }
    }
}
