namespace Custodian.Workflow.Models;

/// <summary>
/// One recorded intervention on a stalled engagement: staff's recovery action taken to
/// unblock it, with the reason and observed outcome. Recorded for history and audit;
/// never mutates the blocker action or advances the engagement stage on its own —
/// gates still apply on any subsequent stage transition.
/// </summary>
public class Intervention
{
    public Guid InterventionId { get; set; } = Guid.NewGuid();

    public string TenantId { get; set; } = string.Empty;

    public Guid EngagementId { get; set; }

    /// <summary>
    /// Optional link to the stall episode that prompted the intervention. Null when the
    /// intervention wasn't tied to a tracked open stall (e.g. proactive client call).
    /// </summary>
    public Guid? StallId { get; set; }

    /// <summary>
    /// Optional link to the client action that was blocking at the time. Advisory only —
    /// the action's status is never changed by recording an intervention.
    /// </summary>
    public Guid? BlockerActionId { get; set; }

    // CSTD-32
    public Guid? MeetingId { get; set; }

    /// <summary>See <see cref="InterventionType"/>.</summary>
    public string Type { get; set; } = InterventionType.RecoveryAction;

    public string Reason { get; set; } = string.Empty;

    /// <summary>See <see cref="InterventionOutcome"/>.</summary>
    public string Outcome { get; set; } = string.Empty;

    public string RecordedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class InterventionType
{
    public const string Meeting = "Meeting";
    public const string RecoveryAction = "RecoveryAction";
}

public static class InterventionOutcome
{
    public const string Recovered = "Recovered";
    public const string Progressing = "Progressing";
    public const string NoChange = "NoChange";
    public const string Escalated = "Escalated";
}