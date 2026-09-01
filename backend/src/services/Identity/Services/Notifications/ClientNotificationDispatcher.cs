using Microsoft.Extensions.Logging;

namespace Custodian.Identity.Services.Notifications;

public sealed class ClientNotificationDispatcher : INotificationDispatcher
{
    private readonly IEnumerable<INotificationDeliveryStrategy> _strategies;
    private readonly IEventDeduplicator _deduplicator;
    private readonly ILogger<ClientNotificationDispatcher> _logger;

    public ClientNotificationDispatcher(
        IEnumerable<INotificationDeliveryStrategy> strategies,
        IEventDeduplicator deduplicator,
        ILogger<ClientNotificationDispatcher> logger)
    {
        _strategies = strategies;
        _deduplicator = deduplicator;
        _logger = logger;
    }

    public async Task DispatchAsync(NotificationContext context, CancellationToken ct = default)
    {
        if (context == null)
        {
            _logger.LogWarning("Dispatch skipped: Null notification context provided");
            return;
        }

        // Idempotency check: Skip duplicate event deliveries
        if (!string.IsNullOrWhiteSpace(context.EventId) && _deduplicator.IsDuplicate(context.EventId))
        {
            _logger.LogInformation("Skipping duplicate notification dispatch for event {EventId}", context.EventId);
            return;
        }

        var enabledChannels = context.Channels ?? Array.Empty<NotificationChannel>();
        var matchingStrategies = _strategies.Where(s => enabledChannels.Contains(s.Channel)).ToList();

        if (matchingStrategies.Count == 0)
        {
            _logger.LogWarning("No delivery strategies matched channels {Channels} for client {ClientId}",
                string.Join(", ", enabledChannels), context.ClientId);
            return;
        }

        var tasks = matchingStrategies.Select(async strategy =>
        {
            try
            {
                if (await strategy.CanHandleAsync(context))
                {
                    var success = await strategy.DeliverAsync(context, ct);
                    if (!success)
                    {
                        _logger.LogWarning("Strategy {Channel} failed delivery for client {ClientId}",
                            strategy.Channel, context.ClientId);
                    }
                }
            }
            catch (Exception ex)
            {
                // Isolate failure so one strategy error doesn't break other channels
                _logger.LogError(ex, "Unhandled exception in delivery strategy {Channel} for client {ClientId}",
                    strategy.Channel, context.ClientId);
            }
        });

        await Task.WhenAll(tasks);
    }
}
