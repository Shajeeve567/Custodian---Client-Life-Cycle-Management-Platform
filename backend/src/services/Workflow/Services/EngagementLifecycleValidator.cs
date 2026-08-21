using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

public static class EngagementLifecycleValidator
{
    /// <summary>
    /// Checks if an engagement can be physically deleted.
    /// Custodian Business Rule: Only Draft (or Cancelled) engagements can be deleted.
    /// Started and Closed engagements cannot be deleted to maintain audit trails.
    /// </summary>
    public static bool CanDelete(EngagementStatus status)
    {
        return status == EngagementStatus.Draft || status == EngagementStatus.Cancelled;
    }

    /// <summary>
    /// Validates status transitions:
    /// - Draft -> Started or Cancelled
    /// - Started -> Closed or Cancelled
    /// - Closed / Cancelled -> Terminal states (cannot change)
    /// </summary>
    public static bool IsValidTransition(EngagementStatus currentStatus, EngagementStatus newStatus)
    {
        if (currentStatus == newStatus)
        {
            return true;
        }

        return currentStatus switch
        {
            EngagementStatus.Draft => newStatus == EngagementStatus.Started || newStatus == EngagementStatus.Cancelled,
            EngagementStatus.Started => newStatus == EngagementStatus.Closed || newStatus == EngagementStatus.Cancelled,
            EngagementStatus.Closed => false,    // Terminal state
            EngagementStatus.Cancelled => false, // Terminal state
            _ => false
        };
    }
}
