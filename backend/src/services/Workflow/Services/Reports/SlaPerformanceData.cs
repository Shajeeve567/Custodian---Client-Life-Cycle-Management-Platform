namespace Custodian.Workflow.Services.Reports;

/// <summary>Where one action stands against its SLA at the report's "now".</summary>
public enum SlaOutcome
{
    CompletedOnTime,
    CompletedLate,
    OpenOnTrack,
    OpenOverdue,
    Cancelled
}

/// <summary>
/// On-time/late/overdue counts for a group of actions (the whole population, a stage, a type, a party).
/// Cancelled actions are counted but excluded from the on-time rate.
/// </summary>
public sealed record SlaCounts(int Total, int CompletedOnTime, int CompletedLate, int OpenOnTrack, int OpenOverdue, int Cancelled)
{
    public static readonly SlaCounts Zero = new(0, 0, 0, 0, 0, 0);

    public int Completed => CompletedOnTime + CompletedLate;

    public int Open => OpenOnTrack + OpenOverdue;

    /// <summary>Completed on time ÷ completed, as a percentage to 1 decimal; null ("n/a") when nothing completed.</summary>
    public double? OnTimeRatePercent =>
        Completed == 0 ? null : Math.Round(CompletedOnTime * 100.0 / Completed, 1, MidpointRounding.AwayFromZero);

    public static SlaCounts From(IEnumerable<SlaActionRow> rows)
    {
        var list = rows as IReadOnlyCollection<SlaActionRow> ?? rows.ToList();
        return new SlaCounts(
            list.Count,
            list.Count(r => r.Outcome == SlaOutcome.CompletedOnTime),
            list.Count(r => r.Outcome == SlaOutcome.CompletedLate),
            list.Count(r => r.Outcome == SlaOutcome.OpenOnTrack),
            list.Count(r => r.Outcome == SlaOutcome.OpenOverdue),
            list.Count(r => r.Outcome == SlaOutcome.Cancelled));
    }
}

/// <summary>
/// One action in the report population. Hours are rounded to 1 decimal. Also the CSV detail row.
/// </summary>
public sealed record SlaActionRow(
    Guid EngagementId,
    Guid ActionId,
    string Title,
    string ActionType,
    int StageNumber,
    string ResponsibleParty,
    string Status,
    DateTime ActivatedAtUtc,
    DateTime DueAtUtc,
    DateTime? CompletedAtUtc,
    SlaOutcome Outcome,
    // CompletedAt − ActivatedAt, for completed actions.
    double? CompletionHours,
    // CompletedAt − DueAt, for late completions.
    double? LatenessHours,
    // now − DueAt, for open overdue actions.
    double? OverdueHours);

public sealed record SlaStageBreakdown(int StageNumber, SlaCounts Counts);

public sealed record SlaGroupBreakdown(string Name, SlaCounts Counts);

/// <summary>Timing figures in hours (1 decimal); null when there is nothing to measure.</summary>
public sealed record SlaTiming(
    double? AverageCompletionHours,
    double? MedianCompletionHours,
    double? AverageLatenessHours,
    double? WorstCurrentOverdueHours);

/// <summary>Stall episodes (CSTD-33 stall records) for the same filters.</summary>
public sealed record SlaStallStatistics(
    // Stalls detected in the date range.
    int Opened,
    // Stalls resolved in the date range.
    int Resolved,
    // Resolved-in-range stalls by resolution (e.g. ActionCompleted), most frequent first.
    IReadOnlyList<SlaGroupBreakdown> ResolvedByResolution,
    // Mean ResolvedAt − DetectedAt of the resolved-in-range stalls, hours to 1 decimal.
    double? AverageResolutionHours,
    // Stalls still open now, regardless of the date range.
    int OpenNow)
{
    public static readonly SlaStallStatistics None = new(0, 0, [], null, 0);
}

/// <summary>
/// CSTD-37-1: the SLA performance numbers, computed from live data before any report layout.
/// The report model (CSTD-37-3) and the tests both read this.
/// </summary>
public sealed record SlaPerformanceData(
    SlaReportFilter Filter,
    DateTimeOffset GeneratedAtUtc,
    SlaCounts Totals,
    SlaTiming Timing,
    IReadOnlyList<SlaStageBreakdown> ByStage,
    IReadOnlyList<SlaGroupBreakdown> ByActionType,
    IReadOnlyList<SlaGroupBreakdown> ByResponsibleParty,
    SlaStallStatistics Stalls,
    // Up to 10 open overdue actions, most overdue first.
    IReadOnlyList<SlaActionRow> TopOverdue,
    // The whole population, for the CSV export.
    IReadOnlyList<SlaActionRow> Actions)
{
    public bool IsEmpty => Totals.Total == 0;
}
