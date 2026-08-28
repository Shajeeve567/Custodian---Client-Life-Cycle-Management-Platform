namespace Custodian.Identity.Services.Notifications;

public interface IEventDeduplicator
{
    /// <summary>
    /// Checks if an event ID has already been seen. 
    /// If not seen, records it and returns false (not duplicate).
    /// If already seen, returns true (is duplicate).
    /// </summary>
    bool IsDuplicate(string eventId);
}
