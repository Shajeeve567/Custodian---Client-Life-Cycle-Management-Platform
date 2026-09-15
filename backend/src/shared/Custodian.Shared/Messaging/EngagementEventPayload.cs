using System.Text.Json;

namespace Custodian.Shared.Messaging;

/// <summary>
/// Wraps engagement-specific fields (EngagementId, Actor) that don't have a
/// dedicated slot on KafkaEnvelope, alongside the original event-specific
/// payload (Data). Used as the T in KafkaEnvelope.Create&lt;T&gt; for engagement
/// lifecycle events (Genesis, StatusChange, StageChange) so a consumer can
/// recover EngagementId/Actor without changing the shared envelope shape.
/// </summary>
public sealed record EngagementEventPayload(Guid EngagementId, string Actor, JsonElement Data);
