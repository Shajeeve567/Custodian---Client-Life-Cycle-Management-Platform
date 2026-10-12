namespace Custodian.Workflow.Services.Reports;

/// <summary>Who the engagement summary is for. Decides only whether <see cref="EngagementSummaryData.Staff"/> is filled.</summary>
public enum EngagementSummaryAudience
{
    /// <summary>The client downloading their own report from the portal.</summary>
    Client,

    /// <summary>An Owner or the responsible Staff member: the same client-safe report plus <see cref="StaffSummaryDetails"/>.</summary>
    Staff
}

/// <summary>
/// CSTD-14 / CSTD-75: the engagement summary report as plain data, built at request time from Workflow
/// and Documents (see <see cref="EngagementSummaryAggregator"/>), then laid out by the report builder.
///
/// Everything here is client-safe by construction: the records below have no tenant id, client id,
/// staff id, internal notes, audit actors, stall/SLA internals or source metadata, so nothing can leak
/// into the client's PDF by mistake. The only staff-facing values live in <see cref="Staff"/>, which is
/// null for <see cref="EngagementSummaryAudience.Client"/>.
/// Every date is UTC (<see cref="DateTimeKind.Utc"/>).
/// </summary>
public sealed record EngagementSummaryData
{
    public required Guid EngagementId { get; init; }

    /// <summary>Short reference shown to the client, e.g. ENG-A1B2C3 (engagements have no name).</summary>
    public required string EngagementCode { get; init; }

    /// <summary>Requester from the JWT (email, else user id), printed as "Prepared for".</summary>
    public required string PreparedFor { get; init; }

    public required EngagementSummaryAudience Audience { get; init; }

    /// <summary>The single "now" the report was computed at.</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required EngagementOverview Overview { get; init; }

    public required IReadOnlyList<SummaryStageRow> Stages { get; init; }

    /// <summary>What the client should do next (client view of the next-action engine), or null when nothing is open.</summary>
    public SummaryNextStep? NextStep { get; init; }

    /// <summary>Open client actions, most urgent first.</summary>
    public required IReadOnlyList<SummaryActionRow> OutstandingActions { get; init; }

    /// <summary>The most recently completed client actions, newest first.</summary>
    public required IReadOnlyList<SummaryCompletedActionRow> RecentlyCompletedActions { get; init; }

    public required IReadOnlyList<SummaryConditionRow> Approvals { get; init; }

    public required IReadOnlyList<SummaryConditionRow> Payments { get; init; }

    public required SummaryDocumentCounts DocumentCounts { get; init; }

    public required IReadOnlyList<SummaryDocumentRow> Documents { get; init; }

    /// <summary>Staff route only; always null for the client.</summary>
    public StaffSummaryDetails? Staff { get; init; }
}

/// <param name="Status">Draft | Started | Closed | Cancelled.</param>
/// <param name="StatusDescription">The portal's client-facing status line (e.g. "Action required: …").</param>
/// <param name="IsFinal">Closed or Cancelled: the final state section is printed.</param>
public sealed record EngagementOverview(
    string Status,
    int CurrentStageNumber,
    string CurrentStageName,
    int ProgressPercentage,
    int CompletedTasks,
    int TotalTasks,
    string StatusDescription,
    DateTime CreatedAtUtc,
    bool IsFinal,
    DateTime? ClosedAtUtc);

/// <param name="Status">Completed | Current | Upcoming.</param>
public sealed record SummaryStageRow(int StageNumber, string Name, string Status);

public sealed record SummaryNextStep(string Title, string? Detail, DateTime? DueAtUtc, bool IsOverdue);

/// <param name="IsAvailable">False while the action's stage has not started (the client can see it but not act yet).</param>
public sealed record SummaryActionRow(
    string Title,
    string Type,
    int StageNumber,
    string Status,
    DateTime? DeadlineUtc,
    bool IsOverdue,
    bool IsAvailable,
    string? RejectionReason);

public sealed record SummaryCompletedActionRow(string Title, string Type, int StageNumber, DateTime? CompletedAtUtc);

/// <param name="Status">Client wording: Pending | Approved | Rejected for approvals, Pending | Satisfied | Rejected for payments.</param>
/// <param name="CompletedAtUtc">When the condition was satisfied; null while open.</param>
public sealed record SummaryConditionRow(
    string Title,
    string Type,
    string Status,
    string RequiredBeforeStage,
    DateTime? DueDateUtc,
    decimal? Amount,
    string? Currency,
    string? PaymentType,
    bool IsOverdue,
    DateTime? CompletedAtUtc,
    string? RejectionReason);

/// <param name="Requested">Document/evidence tasks asked of the client (cancelled ones excluded).</param>
/// <param name="Submitted">Documents on record (soft-deleted ones excluded).</param>
public sealed record SummaryDocumentCounts(int Requested, int Submitted, int Compliant, int Verified, int Rejected)
{
    public static readonly SummaryDocumentCounts Zero = new(0, 0, 0, 0, 0);
}

/// <param name="Status">Client wording: Checking | Under review | Verified | Rejected.</param>
/// <param name="RejectionReason">Only when <paramref name="Status"/> is Rejected.</param>
public sealed record SummaryDocumentRow(string Type, string? FileName, DateTime? UploadedAtUtc, string Status, string? RejectionReason);

/// <summary>Staff-only identifiers, never built for the client route.</summary>
public sealed record StaffSummaryDetails(string ClientId, string ResponsibleStaffId);
