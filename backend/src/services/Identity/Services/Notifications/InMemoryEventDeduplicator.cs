using System.Collections.Concurrent;

namespace Custodian.Identity.Services.Notifications;

public sealed class InMemoryEventDeduplicator : IEventDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedEvents = new();
    private readonly TimeSpan _retentionWindow;

    public InMemoryEventDeduplicator(TimeSpan? retentionWindow = null)
    {
        _retentionWindow = retentionWindow ?? TimeSpan.FromHours(1);
    }

    public bool IsDuplicate(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        CleanupExpired();

        var now = DateTimeOffset.UtcNow;
        return !_processedEvents.TryAdd(eventId, now);
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _retentionWindow;
        foreach (var (eventId, timestamp) in _processedEvents)
        {
            if (timestamp < cutoff)
            {
                _processedEvents.TryRemove(eventId, out _);
            }
        }
    }
}
