using System.Text.Json;
using Confluent.Kafka;
using Custodian.Shared.Contracts;
using Custodian.Shared.Messaging;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Custodian.Workflow.Services.Kafka;

/// <summary>
/// CSTD-19 (19-N5): service-side document sync. Consumes document verification events that the
/// Documents service publishes to the shared "custodian.events" topic and applies the outcome to the
/// linked ClientAction through IClientActionService.ApplyVerificationOutcomeAsync (state machine +
/// ClientActionStatusChanged). Structurally mirrors Audit's KafkaAuditEventConsumer, with the handling
/// split into public methods so it is testable without a broker.
///
/// Delivery is at-least-once: the offset is committed only after a message is handled or deliberately
/// skipped (malformed, unrelated, or a transition the state machine refuses). A failure that may be
/// transient (database unavailable, timeout) leaves the offset uncommitted; the consumer seeks back to
/// the message and retries it with a growing delay, so the outcome is never lost. Handling is
/// idempotent, so redelivery never applies an outcome twice.
///
/// The frontend's second call (WorkflowApi.applyVerification after verify/reject) stays as a fast path
/// for the staff UI; whichever arrives second is a no-op.
/// </summary>
public sealed class DocumentEventsConsumer : BackgroundService
{
    private const string DefaultActor = "documents-service";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly HashSet<string> HandledEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        EventTypes.DocumentVerified,
        EventTypes.DocumentVerificationRejected
    };

    private readonly KafkaConsumerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentEventsConsumer> _logger;

    public DocumentEventsConsumer(
        IOptions<KafkaConsumerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<DocumentEventsConsumer> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ConsumerEnabled)
        {
            _logger.LogInformation("Workflow document events consumer is disabled via configuration.");
            return;
        }

        await Task.Yield(); // Ensure startup isn't blocked

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = Enum.TryParse<AutoOffsetReset>(_options.AutoOffsetReset, true, out var reset)
                ? reset
                : AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        if (!string.IsNullOrWhiteSpace(_options.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(_options.SecurityProtocol, true, out var secProtocol))
        {
            config.SecurityProtocol = secProtocol;
            if (Enum.TryParse<SaslMechanism>(_options.SaslMechanism ?? "Plain", true, out var saslMech))
            {
                config.SaslMechanism = saslMech;
            }
            config.SaslUsername = !string.IsNullOrWhiteSpace(_options.SaslUsername) ? _options.SaslUsername : "$ConnectionString";
            config.SaslPassword = _options.SaslPassword;
        }

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(_options.Topic);
            _logger.LogInformation("Subscribed to Kafka topic: {Topic} as group {GroupId}", _options.Topic, _options.GroupId);
            var consecutiveFailures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message == null)
                    {
                        continue;
                    }

                    if (await HandleAsync(consumeResult.Message.Value, stoppingToken))
                    {
                        consumer.Commit(consumeResult);
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        // Not committed: rewind so the same message is consumed again after a delay.
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
                    _logger.LogWarning(ex, "Kafka consumption error on topic {Topic}", _options.Topic);
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
            _logger.LogError(ex, "Failed to start or maintain Kafka consumer on {BootstrapServers}", _options.BootstrapServers);
        }
    }

    /// <summary>Delay before retrying a message that failed: 1s, 2s, 4s ... capped at 60s.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(consecutiveFailures, 1, 7) - 1)));

    /// <summary>
    /// Handles one message. True when it is done (applied or deliberately skipped) and its offset may be
    /// committed; false when handling failed and the message must be retried (offset stays uncommitted).
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
            _logger.LogError(ex, "Failed to process Kafka document event message; it will be retried.");
            return false;
        }
    }

    /// <summary>
    /// Applies one message. Malformed, unrelated or incomplete messages are logged and skipped (retrying
    /// cannot fix them). Other failures, such as the database being unavailable, are thrown so the
    /// message is retried instead of lost.
    /// </summary>
    public async Task ProcessMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return;
        }

        KafkaEnvelope? envelope;
        DocumentVerificationPayload? payload;
        try
        {
            envelope = JsonSerializer.Deserialize<KafkaEnvelope>(messageJson, JsonOptions);
            if (envelope == null)
            {
                _logger.LogWarning("Discarding unparseable Kafka message.");
                return;
            }

            // Shared topic: only the document verification events concern Workflow.
            if (!HandledEventTypes.Contains(envelope.EventType))
            {
                return;
            }

            payload = EventData(envelope.Payload).Deserialize<DocumentVerificationPayload>(JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding malformed Kafka message.");
            return;
        }

        var tenantId = !string.IsNullOrWhiteSpace(envelope.TenantId) ? envelope.TenantId : payload?.TenantId;
        if (payload == null || payload.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("Discarding {EventType} message {EventId} without document or tenant id.", envelope.EventType, envelope.EventId);
            return;
        }

        var isVerified = string.Equals(envelope.EventType, EventTypes.DocumentVerified, StringComparison.OrdinalIgnoreCase);
        var targetStatus = isVerified ? ClientActionStatus.Completed : ClientActionStatus.Rejected;

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var actionService = scope.ServiceProvider.GetRequiredService<IClientActionService>();

        var linkedActions = await dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId &&
                        a.LinkedDocumentId == payload.DocumentId &&
                        a.Status != ClientActionStatus.Completed &&
                        a.Status != ClientActionStatus.Cancelled)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.ActionId)
            .ToListAsync(ct);

        foreach (var action in linkedActions)
        {
            // Idempotency: the outcome is already applied (redelivery or the frontend dual call).
            if (action.Status == targetStatus)
            {
                continue;
            }

            try
            {
                await actionService.ApplyVerificationOutcomeAsync(
                    action.EngagementId,
                    action.ActionId,
                    tenantId,
                    new ApplyActionVerificationDto
                    {
                        VerificationStatus = isVerified ? DocumentVerificationStatus.Verified : DocumentVerificationStatus.Rejected,
                        VerifiedBy = !string.IsNullOrWhiteSpace(payload.VerifiedBy) ? payload.VerifiedBy : DefaultActor,
                        VerificationReason = isVerified ? payload.Notes : payload.RejectionReason
                    });
            }
            catch (InvalidOperationException ex)
            {
                // Transition not allowed by the state machine (e.g. closed engagement): skip, don't retry forever.
                _logger.LogWarning(ex, "Skipped {EventType} for action {ActionId}: transition not allowed.", envelope.EventType, action.ActionId);
            }
        }
    }

    /// <summary>
    /// The event's own fields. Documents and Workflow wrap them in EngagementEventPayload
    /// {EngagementId, Actor, Data}; older or hand-written messages put them at the top level.
    /// </summary>
    private static JsonElement EventData(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in payload.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(EngagementEventPayload.Data), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value;
                }
            }
        }

        return payload;
    }

    private sealed class DocumentVerificationPayload
    {
        public Guid DocumentId { get; set; }
        public Guid EngagementId { get; set; }
        public string? TenantId { get; set; }
        public string? VerifiedBy { get; set; }
        public string? Notes { get; set; }
        public string? RejectionReason { get; set; }
    }
}
