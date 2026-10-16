using System.Globalization;
using System.Text;
using Custodian.Shared.Reporting.Models;

namespace Custodian.Workflow.Services.Reports;

/// <summary>The engagement summary report (CSTD-14): the shared model plus the engagement it is about.</summary>
public sealed class EngagementSummaryReport : ReportModel
{
    public EngagementSummaryReport(ReportMetadata metadata, IEnumerable<ReportSection> sections, string engagementCode)
        : base(metadata, sections)
    {
        EngagementCode = engagementCode;
    }

    /// <summary>Used in the download file name.</summary>
    public string EngagementCode { get; }
}

/// <summary>
/// CSTD-77: lays <see cref="EngagementSummaryData"/> out as report sections. Pure: no queries, no clock;
/// every value comes from the data, and the only fixed text is headings, labels and "None".
/// The client's copy uses <see cref="ReportAudience.Client"/>, so the PDF header has no tenant id.
/// </summary>
public static class EngagementSummaryReportBuilder
{
    public const string ReportCode = "ENGAGEMENT_SUMMARY";
    public const string Title = "Engagement Summary";
    public const string DataSource = "Workflow and Documents services, live data";
    public const string None = "None";

    public static EngagementSummaryReport Build(EngagementSummaryData data, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(data);
        var isClient = data.Audience == EngagementSummaryAudience.Client;

        var metadata = new ReportMetadata(
            ReportCode,
            Title,
            tenantId,
            data.GeneratedAtUtc,
            data.PreparedFor,
            [new KeyValuePair<string, string>("Engagement", data.EngagementCode)],
            DataSource,
            audience: isClient ? ReportAudience.Client : ReportAudience.Internal);

        var sections = new List<ReportSection>
        {
            Summary(data),
            Stages(data),
            NextStep(data, isClient ? "Your next step" : "Client's next step"),
            OutstandingActions(data),
            RecentlyCompleted(data),
            Approvals(data),
            Payments(data),
            DocumentSummary(data),
            Documents(data, isClient ? "Your documents" : "Client documents")
        };

        if (data.Staff is not null)
        {
            sections.Add(new KeyValueSection("Staff details",
            [
                new ReportKeyValue("Client id", data.Staff.ClientId),
                new ReportKeyValue("Responsible staff id", data.Staff.ResponsibleStaffId)
            ]));
        }

        return new EngagementSummaryReport(metadata, sections, data.EngagementCode);
    }

    private static KeyValueSection Summary(EngagementSummaryData data)
    {
        var o = data.Overview;
        var pairs = new List<ReportKeyValue>
        {
            new("Engagement", data.EngagementCode),
            new("Status", o.Status),
            new("Current stage", $"{o.CurrentStageNumber} of {Math.Max(data.Stages.Count, o.CurrentStageNumber)} · {o.CurrentStageName}"),
            new("Progress", string.Create(CultureInfo.InvariantCulture,
                $"{o.ProgressPercentage}% ({o.CompletedTasks} of {o.TotalTasks} tasks complete)")),
            new("Current position", string.IsNullOrWhiteSpace(o.StatusDescription) ? None : o.StatusDescription),
            new($"Created {ReportTimeZone.ColumnSuffix}", o.CreatedAtUtc)
        };

        if (o.IsFinal)
        {
            pairs.Add(new ReportKeyValue("Final state", o.Status));
            pairs.Add(new ReportKeyValue($"Closed {ReportTimeZone.ColumnSuffix}", (object?)o.ClosedAtUtc ?? None));
        }

        return new KeyValueSection("Engagement summary", pairs);
    }

    private static ReportSection Stages(EngagementSummaryData data) =>
        data.Stages.Count == 0
            ? new EmptySection("Stage progress", None)
            : new TableSection("Stage progress",
                [Number("Stage"), Text("Name", 3), Text("Status", 2)],
                data.Stages.Select(s => Row(s.StageNumber, s.Name, s.Status)));

    private static ReportSection NextStep(EngagementSummaryData data, string title)
    {
        var step = data.NextStep;
        if (step is null) return new EmptySection(title, None);

        return new KeyValueSection(title,
        [
            new ReportKeyValue("Action", step.Title),
            new ReportKeyValue("Details", step.Detail ?? None),
            new ReportKeyValue($"Due {ReportTimeZone.ColumnSuffix}", (object?)step.DueAtUtc ?? None),
            new ReportKeyValue("Overdue", step.IsOverdue)
        ]);
    }

    private static ReportSection OutstandingActions(EngagementSummaryData data)
    {
        const string title = "Outstanding actions";
        if (data.OutstandingActions.Count == 0) return new EmptySection(title, None);

        return new TableSection(title,
            [Text("Action", 3), Text("Type", 1.6f), Number("Stage", 0.8f), Text($"Due {ReportTimeZone.ColumnSuffix}", 2.7f), Text("Status", 1.8f), Text("Reason", 2.1f)],
            data.OutstandingActions.Select(a => Row(
                a.Title,
                SlaPerformanceReportBuilder.TypeLabel(a.Type),
                a.StageNumber,
                a.DeadlineUtc,
                a.IsAvailable ? WithOverdue(a.Status, a.IsOverdue) : $"Opens at stage {a.StageNumber}",
                a.RejectionReason)),
            footnote: "Most urgent first: overdue, then earliest due date.");
    }

    private static ReportSection RecentlyCompleted(EngagementSummaryData data)
    {
        const string title = "Recently completed actions";
        if (data.RecentlyCompletedActions.Count == 0) return new EmptySection(title, None);

        return new TableSection(title,
            [Text("Action", 3), Text("Type", 1.6f), Number("Stage", 0.8f), Text($"Completed {ReportTimeZone.ColumnSuffix}", 2.7f)],
            data.RecentlyCompletedActions.Select(a => Row(a.Title, SlaPerformanceReportBuilder.TypeLabel(a.Type), a.StageNumber, a.CompletedAtUtc)),
            footnote: string.Create(CultureInfo.InvariantCulture,
                $"Newest first; up to {EngagementSummaryAggregator.RecentlyCompletedLimit} actions."));
    }

    private static ReportSection Approvals(EngagementSummaryData data)
    {
        const string title = "Approvals";
        if (data.Approvals.Count == 0) return new EmptySection(title, None);

        return new TableSection(title,
            [Text("Approval", 2.3f), Text("Status", 1.8f), Text("Needed before", 1.4f), Text($"Due {ReportTimeZone.ColumnSuffix}", 2.7f), Text($"Completed {ReportTimeZone.ColumnSuffix}", 2.7f), Text("Reason", 1.8f)],
            data.Approvals.Select(c => Row(
                c.Title, WithOverdue(c.Status, c.IsOverdue), Humanize(c.RequiredBeforeStage), c.DueDateUtc, c.CompletedAtUtc, c.RejectionReason)));
    }

    private static ReportSection Payments(EngagementSummaryData data)
    {
        const string title = "Payments";
        if (data.Payments.Count == 0) return new EmptySection(title, None);

        return new TableSection(title,
            [Text("Payment", 1.9f), Text("Kind", 1.1f), Number("Amount", 1.1f), Text("Currency", 1.3f), Text("Status", 1.8f), Text($"Due {ReportTimeZone.ColumnSuffix}", 2.7f), Text($"Completed {ReportTimeZone.ColumnSuffix}", 2.7f)],
            data.Payments.Select(c => Row(
                c.Title, c.PaymentType, c.Amount, c.Currency, WithOverdue(c.Status, c.IsOverdue), c.DueDateUtc, c.CompletedAtUtc)));
    }

    private static KeyValueSection DocumentSummary(EngagementSummaryData data)
    {
        var counts = data.DocumentCounts;
        return new KeyValueSection("Document summary",
        [
            new ReportKeyValue("Requested", counts.Requested),
            new ReportKeyValue("Submitted", counts.Submitted),
            new ReportKeyValue("Passed automatic checks", counts.Compliant),
            new ReportKeyValue("Verified", counts.Verified),
            new ReportKeyValue("Rejected", counts.Rejected)
        ]);
    }

    private static ReportSection Documents(EngagementSummaryData data, string title)
    {
        if (data.Documents.Count == 0) return new EmptySection(title, None);

        return new TableSection(title,
            [Text("Document type", 2), Text("File name", 2.4f), Text($"Uploaded {ReportTimeZone.ColumnSuffix}", 2.5f), Text("Status", 1.3f), Text("Reason", 2.4f)],
            data.Documents.Select(d => Row(Humanize(d.Type), d.FileName, d.UploadedAtUtc, d.Status, d.RejectionReason)));
    }

    // A4 portrait has no room for a separate overdue column beside two full UTC timestamps.
    private static string WithOverdue(string status, bool isOverdue) => isOverdue ? $"{status} (overdue)" : status;

    /// <summary>"DocumentCollection" → "Document Collection".</summary>
    public static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var text = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1])) text.Append(' ');
            text.Append(value[i]);
        }
        return text.ToString();
    }

    private static object?[] Row(params object?[] cells) => cells;

    private static ReportColumn Text(string header, float width = 1) => new(header, ReportColumnAlignment.Left, width);

    private static ReportColumn Number(string header, float width = 1) => new(header, ReportColumnAlignment.Right, width);
}
