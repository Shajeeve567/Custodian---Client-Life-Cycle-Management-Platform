using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// When a task becomes actionable (CSTD-21 ActivatedAt, which also starts its SLA clock): only once the
/// engagement is Started and the task's stage has been reached. A task added to a Draft engagement, or to a
/// later stage, stays inactive; starting the engagement or advancing the stage activates it
/// (ClientActionService.ActivateStageActionsAsync).
/// </summary>
public static class StageActivation
{
    public static bool IsStageOpen(Engagement? engagement, int stageNumber) =>
        engagement is { Status: EngagementStatus.Started } && stageNumber <= (int)engagement.Stage + 1;

    /// <returns>The activation time to store: the existing one if kept open, now if newly open, else null.</returns>
    public static DateTime? ActivatedAtFor(Engagement? engagement, int stageNumber, DateTime now, DateTime? existing = null) =>
        IsStageOpen(engagement, stageNumber) ? existing ?? now : null;
}

/// <summary>A client tried to act on a task whose stage (or engagement) has not started yet. Maps to 409.</summary>
public sealed class ClientActionNotAvailableException : InvalidOperationException
{
    public ClientActionNotAvailableException(string message) : base(message) { }
}
