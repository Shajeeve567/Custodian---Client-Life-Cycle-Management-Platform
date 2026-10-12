using Custodian.Shared.Contracts;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Reports;

/// <summary>An active condition in its client-safe form, plus when it was satisfied (not on the client DTO).</summary>
public sealed record ClientSafeConditionWithCompletion(ClientSafeConditionDto Condition, DateTime? SatisfiedAtUtc);

/// <summary>
/// Everything the engagement summary is computed from, already fetched by
/// <see cref="EngagementSummaryReportService"/>. The portal dashboard, actions and conditions arrive in
/// their existing client-safe forms; the engagement entity is used only for dates and, on the staff route,
/// the staff-only identifiers.
/// </summary>
public sealed record EngagementSummaryInputs
{
    public required Engagement Engagement { get; init; }
    public required ClientPortalDashboardDto Dashboard { get; init; }

    /// <summary>Client view of the engagement's actions (internal-only actions already excluded).</summary>
    public required IReadOnlyList<ClientActionResponseDto> ClientVisibleActions { get; init; }

    public required IReadOnlyList<ClientSafeConditionWithCompletion> Conditions { get; init; }
    public required IReadOnlyList<DocumentSummaryDto> Documents { get; init; }
    public required EngagementSummaryAudience Audience { get; init; }
    public required string PreparedFor { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// CSTD-76: turns the fetched inputs into <see cref="EngagementSummaryData"/>. Pure (no queries, no clock),
/// so every rule below is unit-tested directly.
/// </summary>
public static class EngagementSummaryAggregator
{
    /// <summary>How many completed actions the "Recently completed" section lists.</summary>
    public const int RecentlyCompletedLimit = 10;

    public const string DocumentChecking = "Checking";
    public const string DocumentUnderReview = "Under review";
    public const string DocumentVerified = "Verified";
    public const string DocumentRejected = "Rejected";

    // Mirrors Documents' ComplianceStatus values (Workflow keeps no reference to the Documents project).
    private const string ComplianceCompliant = "Compliant";
    private const string ComplianceRejected = "Rejected";

    public static EngagementSummaryData Aggregate(EngagementSummaryInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var engagement = inputs.Engagement;
        var dashboard = inputs.Dashboard;
        var nowUtc = inputs.Now.UtcDateTime;
        var isFinal = engagement.Status is EngagementStatus.Closed or EngagementStatus.Cancelled;

        var overview = new EngagementOverview(
            dashboard.Status,
            dashboard.CurrentStageNumber,
            dashboard.CurrentStageName,
            dashboard.ProgressPercentage,
            dashboard.CompletedTasksCount,
            dashboard.TotalTasksCount,
            dashboard.ConditionDescription,
            AsUtc(engagement.CreatedAt),
            isFinal,
            AsUtc(engagement.ClosedAt));

        return new EngagementSummaryData
        {
            EngagementId = engagement.EngagementId,
            EngagementCode = SlaPerformanceReportBuilder.EngagementCode(engagement.EngagementId),
            PreparedFor = inputs.PreparedFor,
            Audience = inputs.Audience,
            GeneratedAtUtc = inputs.Now.ToUniversalTime(),
            Overview = overview,
            Stages = dashboard.Stages.Select(s => new SummaryStageRow(s.StageNumber, s.Name, s.Status)).ToList(),
            NextStep = isFinal ? null : NextStep(dashboard.PrimaryNextAction),
            OutstandingActions = isFinal ? [] : OutstandingActions(dashboard),
            RecentlyCompletedActions = RecentlyCompleted(inputs.ClientVisibleActions),
            Approvals = Conditions(inputs.Conditions, ConditionType.Approval, nowUtc),
            Payments = Conditions(inputs.Conditions, ConditionType.Payment, nowUtc),
            DocumentCounts = CountDocuments(inputs.ClientVisibleActions, inputs.Documents),
            Documents = DocumentRows(inputs.Documents),
            Staff = inputs.Audience == EngagementSummaryAudience.Staff
                ? new StaffSummaryDetails(engagement.ClientId, engagement.StaffId)
                : null
        };
    }

    /// <summary>The portal's primary next action (already the client view of the next-action engine).</summary>
    public static SummaryNextStep? NextStep(ClientSafeActionDto? primary)
    {
        if (primary is null) return null;

        var isRejected = string.Equals(primary.Status, ClientActionStatus.Rejected, StringComparison.OrdinalIgnoreCase);
        var detail = isRejected && !string.IsNullOrWhiteSpace(primary.RejectionReason)
            ? primary.RejectionReason
            : primary.Description;
        return new SummaryNextStep(primary.Title, Blank(detail), AsUtc(primary.DeadlineUtc), primary.IsOverdue);
    }

    /// <summary>
    /// The primary action plus the portal's other pending client actions, most urgent first:
    /// overdue, then earliest deadline, then no deadline; stage and title break ties.
    /// </summary>
    public static IReadOnlyList<SummaryActionRow> OutstandingActions(ClientPortalDashboardDto dashboard)
    {
        var seen = new HashSet<Guid>();
        return dashboard.PendingActions
            .Prepend(dashboard.PrimaryNextAction)
            .OfType<ClientSafeActionDto>()
            .Where(a => seen.Add(a.ActionId))
            .OrderByDescending(a => a.IsOverdue)
            .ThenBy(a => a.DeadlineUtc.HasValue ? 0 : 1)
            .ThenBy(a => a.DeadlineUtc)
            .ThenBy(a => a.StageNumber)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(a => new SummaryActionRow(
                a.Title,
                a.Type,
                a.StageNumber,
                a.Status,
                AsUtc(a.DeadlineUtc),
                a.IsOverdue,
                a.IsAvailable,
                string.Equals(a.Status, ClientActionStatus.Rejected, StringComparison.OrdinalIgnoreCase) ? Blank(a.RejectionReason) : null))
            .ToList();
    }

    public static IReadOnlyList<SummaryCompletedActionRow> RecentlyCompleted(IEnumerable<ClientActionResponseDto> clientVisibleActions) =>
        clientVisibleActions
            .Where(a => !a.IsInternalOnly && string.Equals(a.Status, ClientActionStatus.Completed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.CompletedAt.HasValue ? 0 : 1)
            .ThenByDescending(a => a.CompletedAt)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Take(RecentlyCompletedLimit)
            .Select(a => new SummaryCompletedActionRow(a.Title, a.Type, a.StageNumber, AsUtc(a.CompletedAt)))
            .ToList();

    /// <summary>
    /// Conditions of one type, in the client view's order. Overdue is recomputed against the report's
    /// own "now" (the client DTO uses the wall clock), and only an open condition can be overdue.
    /// </summary>
    public static IReadOnlyList<SummaryConditionRow> Conditions(
        IEnumerable<ClientSafeConditionWithCompletion> conditions, string type, DateTime nowUtc) =>
        conditions
            .Where(c => string.Equals(c.Condition.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(c =>
            {
                var dto = c.Condition;
                var isPending = string.Equals(dto.Status, ConditionStatus.Pending, StringComparison.OrdinalIgnoreCase);
                var isSatisfied = string.Equals(dto.Status, ConditionStatus.Satisfied, StringComparison.OrdinalIgnoreCase);
                var isRejected = string.Equals(dto.Status, ConditionStatus.Rejected, StringComparison.OrdinalIgnoreCase);
                var due = AsUtc(dto.DueDateUtc);

                return new SummaryConditionRow(
                    dto.Title,
                    dto.Type,
                    string.IsNullOrWhiteSpace(dto.ApprovalStatus) ? dto.Status : dto.ApprovalStatus,
                    dto.RequiredBeforeStage,
                    due,
                    dto.Amount,
                    dto.Currency,
                    dto.PaymentType,
                    isPending && due.HasValue && due.Value < nowUtc,
                    isSatisfied ? AsUtc(c.SatisfiedAtUtc) : null,
                    isRejected ? Blank(dto.RejectionReason) : null);
            })
            .ToList();

    /// <summary>
    /// Requested: the client's document/evidence tasks that are not cancelled. The rest count the
    /// documents on record (soft-deleted excluded); a document rejected by the automatic check or by
    /// staff counts once as rejected.
    /// </summary>
    public static SummaryDocumentCounts CountDocuments(
        IEnumerable<ClientActionResponseDto> clientVisibleActions, IEnumerable<DocumentSummaryDto> documents)
    {
        var requested = clientVisibleActions.Count(a =>
            !a.IsInternalOnly &&
            !string.Equals(a.Status, ClientActionStatus.Cancelled, StringComparison.OrdinalIgnoreCase) &&
            (a.LinkedDocumentId.HasValue || ClientActionType.EvidenceTypes.Contains(a.Type ?? string.Empty)));

        var onRecord = documents.Where(d => !d.IsDeleted).ToList();
        return new SummaryDocumentCounts(
            requested,
            onRecord.Count,
            onRecord.Count(d => Is(d.ComplianceStatus, ComplianceCompliant)),
            onRecord.Count(d => Is(d.VerificationStatus, DocumentVerificationStatus.Verified)),
            onRecord.Count(d => DocumentStatus(d) == DocumentRejected));
    }

    /// <summary>Documents on record, newest first, with the client-facing status and (when rejected) reason.</summary>
    public static IReadOnlyList<SummaryDocumentRow> DocumentRows(IEnumerable<DocumentSummaryDto> documents) =>
        documents
            .Where(d => !d.IsDeleted)
            .OrderBy(d => d.UploadedAt.HasValue ? 0 : 1)
            .ThenByDescending(d => d.UploadedAt)
            .ThenBy(d => d.Type, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var status = DocumentStatus(d);
                return new SummaryDocumentRow(
                    d.Type,
                    Blank(d.FileName),
                    AsUtc(d.UploadedAt),
                    status,
                    status == DocumentRejected ? RejectionReason(d) : null);
            })
            .ToList();

    /// <summary>
    /// Client wording for a document: staff rejection or a failed automatic check → Rejected; staff
    /// verified → Verified; passed the automatic check → Under review (awaiting staff); else Checking.
    /// </summary>
    public static string DocumentStatus(DocumentSummaryDto document)
    {
        if (Is(document.VerificationStatus, DocumentVerificationStatus.Rejected) || Is(document.ComplianceStatus, ComplianceRejected))
        {
            return DocumentRejected;
        }
        if (Is(document.VerificationStatus, DocumentVerificationStatus.Verified))
        {
            return DocumentVerified;
        }
        return Is(document.ComplianceStatus, ComplianceCompliant) ? DocumentUnderReview : DocumentChecking;
    }

    // Staff's reason wins over the automatic check's: it is the later decision. Both are shown to clients in the portal.
    private static string? RejectionReason(DocumentSummaryDto document) =>
        Is(document.VerificationStatus, DocumentVerificationStatus.Rejected) && !string.IsNullOrWhiteSpace(document.VerificationReason)
            ? document.VerificationReason
            : Blank(document.RejectionReason);

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Stored times are UTC; MySQL returns them with Kind=Unspecified, so mark them (CSTD-25 offset bug).</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;
}
