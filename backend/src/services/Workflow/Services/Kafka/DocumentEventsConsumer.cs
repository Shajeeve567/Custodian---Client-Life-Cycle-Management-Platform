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
/// CSTD-19 (19-N5): service-side document sync. Consumes document verification events from the
/// shared "custodian.events" topic and applies the outcome to the linked ClientAction through
/// IClientActionService.ApplyVerificationOutcomeAsync (state machine + ClientActionStatusChanged).
/// Structurally mirrors Audit's KafkaAuditEventConsumer: a manual-commit consume loop with the
/// handling split into a public ProcessMessageAsync so it is testable without a broker.
///
/// TODO(CSTD-19 19-N5): the Documents service currently publishes these events to Audit over HTTP
/// only, so nothing reaches this consumer yet. Until Documents publishes KafkaEnvelope messages
/// (EventType = EventTypes.DocumentVerified / DocumentVerificationRejected) the live path remains
/// the frontend dual call (DocumentsApi.verifyDocument/rejectDocument then WorkflowApi.applyVerification).
/// Enable with Kafka__ConsumerEnabled=true once Documents publishes.
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

                    // Commit only after handling (at-least-once); ProcessMessageAsync is idempotent,
                    // so a redelivered message never applies the same outcome twice.
                    consumer.Commit(consumeResult);
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

    public async Task ProcessMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(messageJson, JsonOptions);
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

            var payload = envelope.Payload.Deserialize<DocumentVerificationPayload>(JsonOptions);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to process Kafka document event message");
        }
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
