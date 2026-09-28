using System.Text.Json;
using Confluent.Kafka;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Custodian.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace Custodian.Audit.Services.Kafka;

/// <summary>
/// Consumes engagement lifecycle events (Genesis, StatusChange, StageChange)
/// published by the Workflow service onto the shared "custodian.events" topic,
/// and records them via the same IAuditEventService the HTTP ingestion path uses.
/// Structurally mirrors Identity's KafkaNotificationConsumer: a BackgroundService
/// wrapping a manual-commit consume loop, with the actual message handling split
/// into a public ProcessMessageAsync so it's testable without a real broker.
/// </summary>
public sealed class KafkaAuditEventConsumer : BackgroundService
{
    private static readonly HashSet<string> HandledEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Genesis",
        "StatusChange",
        "StageChange",
        // Owner handed the engagement to another responsible staff member (who then gains access to it).
        "ResponsibleStaffChanged",
        // CSTD-16 (Requirements Collection)
        "RequirementRequested",
        "RequirementSubmitted",
        "RequirementReviewed",
        // CSTD-21 (Client Action Model)
        "ClientActionStatusChanged",
        "ClientActionCreated",
        // Staff-defined stage tasks
        "ClientActionUpdated",
        "StandardChecklistApplied",
        // CSTD-33 (Action SLA & Stall Detection). Dot-case to match what Workflow publishes
        // and what Identity's notification mapper expects.
        "action.overdue",
        // Published once when a persisted stall ends (action completed/cancelled/submitted, deadline
        // extended, engagement closed); pairs with action.overdue via stallId.
        "StallResolved",
        // CSTD-24 (Engagement Condition Management)
        "ConditionAttached",
        "ConditionUpdated",
        "ConditionDeactivated",
        // CSTD-35 (Intervention & Recovery). Dot-case as Workflow publishes it; Identity reads the same
        // event for the client-safe "back on track" notification.
        "intervention.recovered",
        // CSTD-27/28 (Documents), published by the Documents service since it moved to Kafka
        EventTypes.DocumentUploaded,
        EventTypes.DocumentVerified,
        EventTypes.DocumentVerificationRejected,
        EventTypes.DocumentMetadataUpdated,
        EventTypes.DocumentSoftDeleted
    };

    private readonly KafkaOptions _kafkaOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaAuditEventConsumer> _logger;

    public KafkaAuditEventConsumer(
        IOptions<KafkaOptions> kafkaOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<KafkaAuditEventConsumer> logger)
    {
        _kafkaOptions = kafkaOptions.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_kafkaOptions.Enabled)
        {
            _logger.LogInformation("Kafka audit event consumer is disabled via configuration.");
            return;
        }

        await Task.Yield(); // Ensure startup isn't blocked

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaOptions.BootstrapServers,
            GroupId = _kafkaOptions.GroupId,
            AutoOffsetReset = Enum.TryParse<AutoOffsetReset>(_kafkaOptions.AutoOffsetReset, true, out var reset)
                ? reset
                : AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        if (!string.IsNullOrWhiteSpace(_kafkaOptions.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(_kafkaOptions.SecurityProtocol, true, out var secProtocol))
        {
            config.SecurityProtocol = secProtocol;
            if (Enum.TryParse<SaslMechanism>(_kafkaOptions.SaslMechanism ?? "Plain", true, out var saslMech))
            {
                config.SaslMechanism = saslMech;
            }
            config.SaslUsername = !string.IsNullOrWhiteSpace(_kafkaOptions.SaslUsername) ? _kafkaOptions.SaslUsername : "$ConnectionString";
            config.SaslPassword = _kafkaOptions.SaslPassword;
        }

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(_kafkaOptions.Topic);
            var consecutiveFailures = 0;
            _logger.LogInformation("Subscribed to Kafka topic: {Topic} as group {GroupId}", _kafkaOptions.Topic, _kafkaOptions.GroupId);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message == null)
                    {
                        continue;
                    }

                    // Commit offset only after successful handling (at-least-once delivery;
                    // RecordEventAsync's idempotency check is what makes redelivery safe).
                    if (await HandleAsync(consumeResult.Message.Value, stoppingToken))
                    {
                        consumer.Commit(consumeResult);
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        // Not committed: rewind so the event is recorded later instead of lost.
                        consecutiveFailures++;
                        var delay = RetryDelay(consecutiveFailures);
                        _logger.LogWarning(
                            "Retrying Kafka message at {TopicPartitionOffset} in {Delay} (attempt {Attempt}); offset not committed.",
                            consumeResult.TopicPartitionOffset, delay, consecutiveFailures);
                        consumer.Seek(consumeResult.TopicPartitionOffset);
                        await Task.Delay(delay, stoppingToken);
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex, "Kafka consumption error on topic {Topic}", _kafkaOptions.Topic);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error processing Kafka message");
                }
            }

            consumer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start or maintain Kafka consumer on {BootstrapServers}", _kafkaOptions.BootstrapServers);
        }
    }

    /// <summary>Delay before retrying a message that failed: 1s, 2s, 4s ... capped at 60s.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(consecutiveFailures, 1, 7) - 1)));

    /// <summary>
    /// Handles one message. True when it is done (recorded, a duplicate, or deliberately skipped) and its
    /// offset may be committed; false when recording failed (e.g. database unavailable) and the message
    /// must be retried. Previously every failure was committed, so the audit event was silently lost.
    /// </summary>
    public async Task<bool> HandleAsync(string messageJson, CancellationToken ct = default)
    {
        try
        {
            await ProcessMessageAsync(messageJson, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record Kafka audit event; it will be retried.");
            return false;
        }
    }

    /// <summary>
    /// Records one message. Malformed messages and events the service rejects as invalid (missing
    /// fields, bad payload, another tenant's id or chain) are logged and skipped: retrying cannot fix
    /// them. Any other failure is thrown so the message is retried.
    /// </summary>
    public async Task ProcessMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return;
        }

        KafkaEnvelope? envelope;
        EngagementEventPayload enrichedPayload;
        try
        {
            envelope = JsonSerializer.Deserialize<KafkaEnvelope>(messageJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (envelope == null)
            {
                _logger.LogWarning("Discarding unparseable Kafka message: {RawJson}", messageJson);
                return;
            }

            // This topic is shared with other consumers (e.g. Identity's notification
            // consumer reads the same stream for different event types) — only handle
            // the event types published for engagement audit tracking.
            if (!HandledEventTypes.Contains(envelope.EventType))
            {
                return;
            }

            enrichedPayload = envelope.ReadPayload<EngagementEventPayload>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Discarding malformed Kafka audit message.");
            return;
        }

        var tenantId = ResolveTenantId(envelope.TenantId);
        var eventId = Guid.TryParse(envelope.EventId, out var parsedEventId) ? parsedEventId : (Guid?)null;

        var request = new CreateAuditEventRequest
        {
            EventId = eventId,
            EngagementId = enrichedPayload.EngagementId,
            TenantId = tenantId,
            Actor = enrichedPayload.Actor,
            Type = envelope.EventType,
            Payload = JsonSerializer.Serialize(enrichedPayload.Data)
        };

        using var scope = _scopeFactory.CreateScope();
        var eventService = scope.ServiceProvider.GetRequiredService<IAuditEventService>();
        try
        {
            await eventService.RecordEventAsync(request, tenantId);
        }
        catch (Exception ex) when (ex is ArgumentException or Custodian.Audit.Repositories.AuditChainConflictException)
        {
            _logger.LogWarning(ex, "Discarding invalid {EventType} audit event {EventId}.", envelope.EventType, envelope.EventId);
        }
    }

    private static Guid ResolveTenantId(string tenantId)
    {
        if (Guid.TryParse(tenantId, out var parsed))
        {
            return parsed;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Guid.Empty;
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(tenantId));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
