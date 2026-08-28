using System.Text.Json;
using Confluent.Kafka;
using Custodian.Identity.Services.Notifications;
using Custodian.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodian.Identity.Services.Kafka;

public sealed class KafkaNotificationConsumer : BackgroundService
{
    private readonly KafkaOptions _kafkaOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaNotificationConsumer> _logger;

    public KafkaNotificationConsumer(
        IOptions<KafkaOptions> kafkaOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<KafkaNotificationConsumer> logger)
    {
        _kafkaOptions = kafkaOptions.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_kafkaOptions.Enabled)
        {
            _logger.LogInformation("Kafka notification consumer is disabled via configuration.");
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
            _logger.LogInformation("Subscribed to Kafka topic: {Topic}", _kafkaOptions.Topic);

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

                    // Commit offset after successful handling
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

            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

            // Extract context from envelope
            var tenantId = Guid.TryParse(envelope.TenantId, out var parsedTenant) ? parsedTenant : Guid.Empty;
            var clientId = Guid.Empty;
            var messageText = $"Event: {envelope.EventType}";

            if (envelope.Payload.ValueKind == JsonValueKind.Object)
            {
                if (envelope.Payload.TryGetProperty("clientId", out var clientProp) && clientProp.TryGetGuid(out var parsedClient))
                {
                    clientId = parsedClient;
                }
                else if (envelope.Payload.TryGetProperty("ClientId", out var clientProp2) && clientProp2.TryGetGuid(out var parsedClient2))
                {
                    clientId = parsedClient2;
                }

                if (envelope.Payload.TryGetProperty("message", out var msgProp))
                {
                    messageText = msgProp.GetString() ?? messageText;
                }
                else if (envelope.Payload.TryGetProperty("Message", out var msgProp2))
                {
                    messageText = msgProp2.GetString() ?? messageText;
                }
            }

            var context = new NotificationContext
            {
                EventId = envelope.EventId,
                TenantId = tenantId,
                ClientId = clientId,
                SourceEventType = envelope.EventType,
                Message = messageText,
                Subject = $"Update: {envelope.EventType}"
            };

            await dispatcher.DispatchAsync(context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Kafka envelope message");
        }
    }
}
