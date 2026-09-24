using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// Authoritative state machine for ClientAction lifecycle transitions (CSTD-21).
/// Enforces valid state mutations and terminal states.
/// </summary>
public static class ClientActionStateMachine
{
    private static readonly HashSet<(string From, string To)> AllowedTransitions = new()
    {
        // Pending
        (ClientActionStatus.Pending, ClientActionStatus.Uploaded),
        (ClientActionStatus.Pending, ClientActionStatus.Completed),
        (ClientActionStatus.Pending, ClientActionStatus.Rejected),
        (ClientActionStatus.Pending, ClientActionStatus.Cancelled),

        // Uploaded
        (ClientActionStatus.Uploaded, ClientActionStatus.Completed),
        (ClientActionStatus.Uploaded, ClientActionStatus.Rejected),
        (ClientActionStatus.Uploaded, ClientActionStatus.Cancelled),

        // Rejected
        (ClientActionStatus.Rejected, ClientActionStatus.Uploaded),
        (ClientActionStatus.Rejected, ClientActionStatus.Pending),
        (ClientActionStatus.Rejected, ClientActionStatus.Completed),
        (ClientActionStatus.Rejected, ClientActionStatus.Cancelled),

        // Completed (only review rejection of submitted action/requirement can reopen to Rejected)
        (ClientActionStatus.Completed, ClientActionStatus.Rejected)
    };

    /// <summary>
    /// Checks whether transitioning from <paramref name="fromStatus"/> to <paramref name="toStatus"/> is allowed.
    /// Same-to-same transitions are treated as valid no-ops.
    /// </summary>
    public static bool CanTransition(string fromStatus, string toStatus)
    {
        if (string.Equals(fromStatus, toStatus, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AllowedTransitions.Contains((fromStatus, toStatus));
    }

    /// <summary>
    /// Enforces that the transition from <paramref name="fromStatus"/> to <paramref name="toStatus"/> is allowed.
    /// Throws <see cref="InvalidOperationException"/> if the transition is prohibited.
    /// </summary>
    public static void EnsureCanTransition(string fromStatus, string toStatus)
    {
        if (string.Equals(fromStatus, toStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!AllowedTransitions.Contains((fromStatus, toStatus)))
        {
            throw new InvalidOperationException($"Invalid action status transition from '{fromStatus}' to '{toStatus}'.");
        }
    }
}
