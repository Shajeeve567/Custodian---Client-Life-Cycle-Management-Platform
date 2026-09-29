using System.Globalization;
using Custodian.Shared.Reporting.Models;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Reports;

/// <summary>The SLA performance report: the shared model plus the per-action table the CSV exports.</summary>
public sealed class SlaPerformanceReport : ReportModel
{
    public SlaPerformanceReport(ReportMetadata metadata, IEnumerable<ReportSection> sections, TableSection detail)
        : base(metadata, sections)
    {
        Detail = detail;
    }

    /// <summary>One row per action in the population (the CSV download).</summary>
    public TableSection Detail { get; }
}

/// <summary>
/// CSTD-37-3: lays the computed <see cref="SlaPerformanceData"/> out as report sections. Pure: no queries,
/// no clock (the data carries its own "now"); every number comes from the data.
/// </summary>
public static class SlaPerformanceReportBuilder
{
    public const string ReportCode = "SLA_PERFORMANCE";
    public const string Title = "SLA Performance";
    public const string DataSource = "Workflow service live database";
    private const string NotApplicable = "n/a";

    private static readonly IReadOnlyDictionary<string, string> TypeLabels = new Dictionary<string, string>
    {
        [ClientActionType.DocumentUpload] = "Document upload",
        [ClientActionType.KycDocument] = "KYC document",
        [ClientActionType.SignAgreement] = "Sign agreement",
        [ClientActionType.ProofOfAddress] = "Proof of address",
        [ClientActionType.CustomTask] = "Custom task",
        [ClientActionType.Requirement] = "Request information",
        [ClientActionType.Approval] = "Approval",
        [ClientActionType.Payment] = "Payment",
        [ClientActionType.Meeting] = "Meeting"
    };

    private static readonly ReportColumn[] CountColumns =
    [
        Number("Total"), Number("On time"), Number("Late"), Number("On track"), Number("Overdue"), Number("Cancelled"), Number("On-time rate")
    ];

    public static SlaPerformanceReport Build(SlaPerformanceData data, string tenantId, string generatedBy)
    {
        ArgumentNullException.ThrowIfNull(data);

        var metadata = new ReportMetadata(
            ReportCode, Title, tenantId, data.GeneratedAtUtc, generatedBy, AppliedFilters(data.Filter), DataSource);

        var sections = new List<ReportSection>
        {
            Summary(data),
            Outcomes(data),
            Timing(data),
            ByStage(data),
            ByGroup("By action type", "Action type", data.ByActionType, TypeLabel, data.IsEmpty),
            ByGroup("By responsible party", "Party", data.ByResponsibleParty, name => name, data.IsEmpty),
            StallStatistics(data.Stalls),
            StallResolutions(data.Stalls),
            TopOverdue(data),
            Definitions()
        };

        return new SlaPerformanceReport(metadata, sections, Detail(data));
    }

    public static string EngagementCode(Guid engagementId) => $"ENG-{engagementId.ToString("N")[..6].ToUpperInvariant()}";

    public static string TypeLabel(string type) => TypeLabels.TryGetValue(type, out var label) ? label : type;

    public static string StageLabel(int stageNumber) =>
        $"{stageNumber} · {Humanize(SlaReportFilter.StageFor(stageNumber).ToString())}";

    private static IEnumerable<KeyValuePair<string, string>> AppliedFilters(SlaReportFilter filter) =>
    [
        new("From", filter.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        new("To", filter.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        new("Engagement", filter.EngagementId is { } id ? EngagementCode(id) : "All"),
        new("Stage", filter.StageNumber is { } stage ? StageLabel(stage) : "All"),
        new("Responsible staff", filter.StaffId ?? "All"),
        new("Action type", filter.ActionType is { } type ? TypeLabel(type) : "All")
    ];

    private static ReportSection Summary(SlaPerformanceData data) => new KeyValueSection("Summary",
    [
        new("Total actions", data.Totals.Total),
        new("Completed on time", data.Totals.CompletedOnTime),
        new("Completed late", data.Totals.CompletedLate),
        new("Open – on track", data.Totals.OpenOnTrack),
        new("Open – overdue", data.Totals.OpenOverdue),
        new("Cancelled", data.Totals.Cancelled),
        new("On-time rate", Rate(data.Totals))
    ]);

    private static ReportSection Outcomes(SlaPerformanceData data)
    {
        const string title = "On time vs late vs overdue";
        if (data.IsEmpty) return new EmptySection(title);

        var totals = data.Totals;
        object?[] Row(string label, int count) =>
            [label, count, Percent(count * 100.0 / totals.Total)];

        return new TableSection(title,
            [Label("Outcome"), Number("Actions"), Number("Share of all actions")],
            [
                Row("Completed on time", totals.CompletedOnTime),
                Row("Completed late", totals.CompletedLate),
                Row("Open – on track", totals.OpenOnTrack),
                Row("Open – overdue", totals.OpenOverdue),
                Row("Cancelled", totals.Cancelled)
            ]);
    }

    private static ReportSection Timing(SlaPerformanceData data) => new KeyValueSection("Timing (hours)",
    [
        new("Average completion time", Hours(data.Timing.AverageCompletionHours)),
        new("Median completion time", Hours(data.Timing.MedianCompletionHours)),
        new("Average lateness of late completions", Hours(data.Timing.AverageLatenessHours)),
        new("Worst current overdue", Hours(data.Timing.WorstCurrentOverdueHours))
    ]);

    private static ReportSection ByStage(SlaPerformanceData data)
    {
        const string title = "By stage";
        if (data.IsEmpty) return new EmptySection(title);
        return new TableSection(title,
            [Label("Stage"), .. CountColumns],
            data.ByStage.Select(s => CountRow(StageLabel(s.StageNumber), s.Counts)));
    }

    private static ReportSection ByGroup(
        string title, string header, IReadOnlyList<SlaGroupBreakdown> groups, Func<string, string> label, bool isEmpty)
    {
        if (isEmpty) return new EmptySection(title);
        return new TableSection(title,
            [Label(header), .. CountColumns],
            groups.Select(g => CountRow(label(g.Name), g.Counts)));
    }

    private static ReportSection StallStatistics(SlaStallStatistics stalls) => new KeyValueSection("Stall statistics",
    [
        new("Stalls opened in the period", stalls.Opened),
        new("Stalls resolved in the period", stalls.Resolved),
        new("Average time to resolve (hours)", Hours(stalls.AverageResolutionHours)),
        new("Stalls open now", stalls.OpenNow)
    ]);

    private static ReportSection StallResolutions(SlaStallStatistics stalls)
    {
        const string title = "Stalls resolved, by how";
        if (stalls.ResolvedByResolution.Count == 0)
        {
            return new EmptySection(title, "No stalls were resolved in this period.");
        }
        return new TableSection(title,
            [Label("Resolution"), Number("Stalls")],
            stalls.ResolvedByResolution.Select(r => new object?[] { Humanize(r.Name), r.Counts.Total }));
    }

    private static ReportSection TopOverdue(SlaPerformanceData data)
    {
        const string title = "Top 10 overdue now";
        if (data.TopOverdue.Count == 0) return new EmptySection(title, "No open actions are overdue.");
        return new TableSection(title,
            [new ReportColumn("Engagement", Width: 1.3f), Label("Action"), new ReportColumn("Stage", Width: 1.6f), new ReportColumn("Party"), Number("Overdue by (hours)")],
            data.TopOverdue.Select(r => new object?[]
            {
                EngagementCode(r.EngagementId), r.Title, StageLabel(r.StageNumber), r.ResponsibleParty, Hours(r.OverdueHours)
            }));
    }

    private static ReportSection Definitions() => new TextSection("Definitions",
    [
        "Actions counted: every action that became actionable (its stage opened) between From and To, inclusive, narrowed by the filters shown above. Cancelled actions are counted but left out of the on-time rate.",
        "Due time: the action's deadline if one was set, otherwise the time it became actionable plus the stage SLA. This is the same rule the stall queue uses. If a deadline was extended, the new deadline is used.",
        "On time: completed at or before the due time. Late: completed after it. Overdue: still open (pending, awaiting review or sent back) after the due time; on track: still open before it.",
        "On-time rate: completed on time ÷ all completed actions; n/a when nothing was completed. Completion time: completed − became actionable. Lateness: completed − due, for late completions only.",
        "Stalls: an open action past its due time. Opened/resolved counts use the period; 'open now' is the current state. The stall queue lists client tasks only, so compare it with the Client row of 'By responsible party'.",
        "All times are UTC; hours are rounded to one decimal."
    ]);

    private static TableSection Detail(SlaPerformanceData data) => new(
        "Actions",
        [
            new ReportColumn("Engagement"), new ReportColumn("Engagement id"), new ReportColumn("Action id"), new ReportColumn("Action"),
            new ReportColumn("Action type"), Number("Stage"), new ReportColumn("Responsible party"), new ReportColumn("Status"),
            new ReportColumn("Activated (UTC)"), new ReportColumn("Due (UTC)"), new ReportColumn("Completed (UTC)"),
            new ReportColumn("SLA outcome"), Number("Completion hours"), Number("Lateness hours"), Number("Overdue hours")
        ],
        data.Actions.Select(r => new object?[]
        {
            EngagementCode(r.EngagementId), r.EngagementId, r.ActionId, r.Title, r.ActionType, r.StageNumber,
            r.ResponsibleParty, r.Status, r.ActivatedAtUtc, r.DueAtUtc, r.CompletedAtUtc,
            Humanize(r.Outcome.ToString()), r.CompletionHours, r.LatenessHours, r.OverdueHours
        }));

    private static object?[] CountRow(string label, SlaCounts c) =>
        [label, c.Total, c.CompletedOnTime, c.CompletedLate, c.OpenOnTrack, c.OpenOverdue, c.Cancelled, Rate(c)];

    private static string Rate(SlaCounts counts) =>
        counts.OnTimeRatePercent is { } rate ? Percent(rate) : NotApplicable;

    // PDF sections show hours and percentages with one decimal ("50.0", not "50"); the CSV detail keeps raw numbers.
    private static string Hours(double? hours) =>
        hours.HasValue ? hours.Value.ToString("0.0", CultureInfo.InvariantCulture) : NotApplicable;

    private static string Percent(double value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static ReportColumn Number(string header) => new(header, ReportColumnAlignment.Right);

    private static ReportColumn Label(string header) => new(header, Width: 2);

    /// <summary>"DocumentCollection" → "Document collection".</summary>
    private static string Humanize(string value)
    {
        var chars = new List<char>();
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && char.IsLower(value[i - 1])) chars.Add(' ');
            chars.Add(i == 0 ? value[i] : char.ToLowerInvariant(value[i]));
        }
        return new string(chars.ToArray());
    }
}
