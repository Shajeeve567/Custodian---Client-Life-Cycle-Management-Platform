using System.Text.Json;

namespace Custodian.Shared.Messaging;

public sealed record KafkaEnvelope(
    string EventId,
    string EventType,
    string TenantId,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload)
{
    public static KafkaEnvelope Create<T>(string eventType, string tenantId, T payload) =>
        new(
            EventId: Guid.NewGuid().ToString("N"),
            EventType: eventType,
            TenantId: tenantId,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Payload: JsonSerializer.SerializeToElement(payload));

    public T ReadPayload<T>() =>
        Payload.Deserialize<T>() ?? throw new InvalidOperationException($"Payload cannot be deserialized to {typeof(T).Name}.");
}