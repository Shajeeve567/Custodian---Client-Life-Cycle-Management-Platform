namespace Custodian.Workflow.DTOs;

/// <summary>
/// Output contract for next-action orchestration (CSTD-19).
/// Deterministically computed from live workflow state.
/// </summary>
public sealed class NextActionResult
{
    public Guid EngagementId { get; init; }

    /// <summary>Draft | Started | Closed | Cancelled</summary>
    public string EngagementStatus { get; init; } = string.Empty;

    /// <summary>Current stage enum name (Onboarding, DocumentCollection, Verification, Execution, Closure)</summary>
    public string CurrentStage { get; init; } = string.Empty;

    /// <summary>
    /// Overall state: Closed | NotStarted | ClientActionRequired | AwaitingStaff | ReadyToAdvance | BlockedExternal | AllComplete
    /// </summary>
    public string OverallState { get; init; } = string.Empty;

    /// <summary>The single highest-priority next action item, or null if none open / closed.</summary>
    public NextActionItem? PrimaryAction { get; init; }

    /// <summary>All other active blockers preventing progress, in priority order.</summary>
    public IReadOnlyList<NextActionItem> Blockers { get; init; } = Array.Empty<NextActionItem>();

    /// <summary>Gate check evaluation for advancing to the next stage (Staff view only; null for client view).</summary>
    public GateSummary? NextStageGate { get; init; }

    /// <summary>
    /// Active, not-yet-satisfied conditions gating a stage after the next one. Informational only:
    /// never blockers and never affect OverallState. Staff view only (empty for the client view).
    /// </summary>
    public IReadOnlyList<NextActionItem> UpcomingConditions { get; init; } = Array.Empty<NextActionItem>();

    /// <summary>True if the engagement is stalled (CSTD-33 / IStallService).</summary>
    public bool IsStalled { get; init; }

    public DateTimeOffset EvaluatedAtUtc { get; init; }
}

public sealed class NextActionItem
{
    /// <summary>
    /// RequirementSubmission | RequirementReview | DocumentUpload | DocumentResubmission
    /// | DocumentVerification | ConditionApproval | ConditionPayment | ClientTask | StaffTask
    /// | AdvanceStage | Unavailable
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Client | Staff</summary>
    public string ResponsibleParty { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Human-readable explanation of why this item is next or why progress is blocked.</summary>
    public string Reason { get; init; } = string.Empty;

    public Guid? ActionId { get; init; }

    public string? SourceType { get; init; }

    /// <summary>Underlying source entity ID (e.g. RequirementId, ConditionId, DocumentId). Staff view only.</summary>
    public Guid? SourceId { get; init; }

    public int? StageNumber { get; init; }

    public DateTime? DueAtUtc { get; init; }

    public bool IsOverdue { get; init; }

    /// <summary>Time span since deadline passed. Staff view only.</summary>
    public TimeSpan? OverdueBy { get; init; }

    /// <summary>The priority rule rank (0-14) that selected this item. Staff view only.</summary>
    public int PriorityRank { get; init; }
}

public sealed class GateSummary
{
    public string TargetStage { get; init; } = string.Empty;
    public bool IsSatisfied { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public static class NextActionKind
{
    public const string RequirementSubmission = "RequirementSubmission";
    public const string RequirementReview = "RequirementReview";
    public const string DocumentUpload = "DocumentUpload";
    public const string DocumentResubmission = "DocumentResubmission";
    public const string DocumentVerification = "DocumentVerification";
    public const string ConditionApproval = "ConditionApproval";
    public const string ConditionPayment = "ConditionPayment";
    public const string ClientTask = "ClientTask";
    public const string StaffTask = "StaffTask";
    public const string AdvanceStage = "AdvanceStage";
    public const string Unavailable = "Unavailable";
}

public static class ResponsibleParty
{
    public const string Client = "Client";
    public const string Staff = "Staff";
}

public static class OverallState
{
    public const string Closed = "Closed";
    public const string NotStarted = "NotStarted";
    public const string ClientActionRequired = "ClientActionRequired";
    public const string AwaitingStaff = "AwaitingStaff";
    public const string ReadyToAdvance = "ReadyToAdvance";
    public const string BlockedExternal = "BlockedExternal";
    public const string AllComplete = "AllComplete";
}
