namespace Custodian.Workflow.Models;

/// <summary>
/// CSTD-33 (33-N1): one stall episode: an action that went past its SLA deadline. Persisting it is
/// what makes the action.overdue event fire exactly once per episode, across restarts and across
/// service instances (the previous in-memory de-duplication forgot everything on restart).
/// Staff-only data: never exposed to clients.
/// </summary>
public class StallRecord
{
    public Guid StallId { get; set; } = Guid.NewGuid();

    public string TenantId { get; set; } = string.Empty;

    public Guid EngagementId { get; set; }

    public Guid ActionId { get; set; }

    /// <summary>
    /// Equals <see cref="ActionId"/> while the stall is open and null once resolved. A unique index on
    /// it allows at most one open stall per action even when two requests detect it at the same time
    /// (MySQL unique indexes ignore NULLs, so any number of resolved stalls can share the action).
    /// </summary>
    public Guid? OpenActionId { get; set; }

    /// <summary>The effective deadline the action missed.</summary>
    public DateTime DueAtUtc { get; set; }

    public DateTime DetectedAtUtc { get; set; }

    public DateTime? OverdueEventPublishedAt { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>See <see cref="StallResolution"/>.</summary>
    public string? Resolution { get; set; }

    public bool IsOpen => ResolvedAtUtc == null;

    public void Resolve(string resolution, DateTime nowUtc)
    {
        ResolvedAtUtc = nowUtc;
        Resolution = resolution;
        OpenActionId = null;
    }
}

public static class StallResolution
{
    public const string ActionCompleted = "ActionCompleted";
    public const string ActionCancelled = "ActionCancelled";
    /// <summary>The client uploaded evidence: the task now waits on staff review, not on the client.</summary>
    public const string ActionSubmitted = "ActionSubmitted";
    public const string DeadlineExtended = "DeadlineExtended";
    public const string EngagementClosed = "EngagementClosed";
    /// <summary>The task was reassigned to staff or made internal: it no longer waits on the client.</summary>
    public const string Reassigned = "Reassigned";
}
