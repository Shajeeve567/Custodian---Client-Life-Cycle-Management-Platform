using System.Text.Json;
using Confluent.Kafka;
using Custodian.Identity.Services.Notifications;
using Custodian.Shared.Messaging;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

        var config = BuildConsumerConfig(_kafkaOptions);

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

    public static ConsumerConfig BuildConsumerConfig(KafkaOptions options)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            AutoOffsetReset = Enum.TryParse<AutoOffsetReset>(options.AutoOffsetReset, true, out var reset)
                ? reset
                : AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, true, out var secProtocol))
        {
            config.SecurityProtocol = secProtocol;
            if (Enum.TryParse<SaslMechanism>(options.SaslMechanism ?? "Plain", true, out var saslMech))
            {
                config.SaslMechanism = saslMech;
            }
            config.SaslUsername = !string.IsNullOrWhiteSpace(options.SaslUsername) ? options.SaslUsername : "$ConnectionString";
            config.SaslPassword = options.SaslPassword;
        }

        return config;
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

            _logger.LogInformation("Consumed envelope: type={EventType} tenant={TenantId} eventId={EventId}",
                envelope.EventType, envelope.TenantId, envelope.EventId);

            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
            var mapper = scope.ServiceProvider.GetRequiredService<Custodian.Identity.Services.Notifications.Mappers.IEventToMessageMapper>();

            var mapped = mapper.MapToClientSafeMessage(envelope);
            if (mapped == null)
            {
                // Not client-facing (internal task, checklist or audit-only event): nothing to send.
                return;
            }

            if (mapped.ClientId == Guid.Empty && string.IsNullOrWhiteSpace(mapped.ClientEmail))
            {
                _logger.LogWarning("Client-facing event {EventType} {EventId} has no clientId; notification not sent.",
                    envelope.EventType, envelope.EventId);
                return;
            }
            var tenantId = StringToGuid(envelope.TenantId);
            var context = new NotificationContext
            {
                EventId = envelope.EventId,
                TenantId = tenantId,
                ClientId = mapped.ClientId,
                ClientEmail = mapped.ClientEmail,
                SourceEventType = envelope.EventType,
                Message = mapped.Message,
                Subject = mapped.Subject,
                Channels = new[] { NotificationChannel.InAppPortal, NotificationChannel.Email }
            };

            _logger.LogInformation("Dispatching: subject={Subject} clientId={ClientId} email={Email}",
                mapped.Subject, mapped.ClientId, mapped.ClientEmail);

            await dispatcher.DispatchAsync(context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Kafka envelope message");
        }
    }

    private static Guid StringToGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
        if (Guid.TryParse(value, out var parsed)) return parsed;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}