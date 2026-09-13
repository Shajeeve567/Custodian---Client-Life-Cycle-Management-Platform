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
        "StageChange"
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

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(_kafkaOptions.Topic);
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

                    await ProcessMessageAsync(consumeResult.Message.Value, stoppingToken);

                    // Commit offset only after successful handling (at-least-once delivery;
                    // RecordEventAsync's idempotency check is what makes redelivery safe).
                    consumer.Commit(consumeResult);
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

    public async Task ProcessMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(messageJson, new JsonSerializerOptions
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
            // the event types Workflow publishes for engagement audit tracking.
            if (!HandledEventTypes.Contains(envelope.EventType))
            {
                return;
            }

            var enrichedPayload = envelope.ReadPayload<EngagementEventPayload>();
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
            await eventService.RecordEventAsync(request, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Kafka audit envelope message");
        }
    }

    /// <summary>
    /// Mirrors AuditEventsController.StringToGuid: envelope.TenantId is a plain
    /// string that may or may not be a real Guid, so parse it if possible, else
    /// deterministically hash it into one (same fallback the HTTP path already uses).
    /// </summary>
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
