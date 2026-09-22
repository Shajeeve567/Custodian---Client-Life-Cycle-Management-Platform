using System.Collections.Concurrent;

namespace Custodian.Workflow.Services;

/// <summary>
/// Remembers which (engagementId, actionId, deadline) tuples have already emitted
/// an action.overdue event, so repeated reads of the same stall don't spam the topic.
/// In-memory only — resets on restart, acceptable for MVP since notification delivery
/// is idempotent enough at the consumer side (see Identity/KafkaNotificationConsumer).
/// Keyed by deadline so extending a deadline allows a new event to fire later.
/// </summary>
public interface IStallEventDeduplicator
{
    bool HasFired(Guid engagementId, Guid actionId, DateTime deadlineUtc);
    void MarkFired(Guid engagementId, Guid actionId, DateTime deadlineUtc);
}

public sealed class StallEventDeduplicator : IStallEventDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public bool HasFired(Guid engagementId, Guid actionId, DateTime deadlineUtc)
    {
        CleanupExpired();
        return _seen.ContainsKey(Key(engagementId, actionId, deadlineUtc));
    }

    public void MarkFired(Guid engagementId, Guid actionId, DateTime deadlineUtc)
    {
        _seen[Key(engagementId, actionId, deadlineUtc)] = DateTimeOffset.UtcNow;
    }

    private static string Key(Guid engagementId, Guid actionId, DateTime deadlineUtc) =>
        $"{engagementId:N}:{actionId:N}:{deadlineUtc:O}";

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (k, ts) in _seen)
        {
            if (ts < cutoff) _seen.TryRemove(k, out _);
        }
    }
}