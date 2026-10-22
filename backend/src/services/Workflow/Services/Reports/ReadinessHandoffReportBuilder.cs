using System.Globalization;
using Custodian.Shared.Reporting.Models;
using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services.Reports;

/// <summary>The Delivery Readiness & Handoff report model (CSTD-38).</summary>
public sealed class ReadinessHandoffReport : ReportModel
{
    public ReadinessHandoffReport(ReportMetadata metadata, IEnumerable<ReportSection> sections)
        : base(metadata, sections) { }
}

/// <summary>
/// CSTD-38: lays out the compiled report data. Pure — no queries, no clock. Every value comes from
/// the data, and the top-line verdict is <see cref="ReadinessHandoffReportData.IsReady"/>, so an
/// unavailable dependency can never render as Ready.
/// </summary>
public static class ReadinessHandoffReportBuilder
{
    public const string ReportCode = "DELIVERY_READINESS_HANDOFF";
    public const string Title = "Delivery Readiness & Handoff Summary";
    public const string DataSource = "Workflow service live database";
    private const string NotApplicable = "n/a";

    public static ReadinessHandoffReport Build(ReadinessHandoffReportData data, string tenantId, string generatedBy)
    {
        ArgumentNullException.ThrowIfNull(data);

        var metadata = new ReportMetadata(
            ReportCode, Title, tenantId, data.GeneratedAtUtc, generatedBy, AppliedFilters(data), DataSource);

        var sections = new List<ReportSection>
        {
            Summary(data),
            ReadinessVerdict(data),
            ReadinessBlockers(data),
            DocumentVerification(data),
            ApprovalConditions(data),
            PaymentConditions(data),
            HandoffContext(data),
            ClosureContext(data),
            Definitions()
        };

        return new ReadinessHandoffReport(metadata, sections);
    }

    public static string EngagementCode(Guid engagementId) =>
        $"ENG-{engagementId.ToString("N")[..6].ToUpperInvariant()}";

    private static IEnumerable<KeyValuePair<string, string>> AppliedFilters(ReadinessHandoffReportData data) =>
    [
        new("Engagement", EngagementCode(data.Engagement.EngagementId)),
        new($"Generated at {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(data.GeneratedAtUtc.UtcDateTime))
    ];

    private static ReportSection Summary(ReadinessHandoffReportData data) => new KeyValueSection("Engagement",
    [
        new("Engagement", EngagementCode(data.Engagement.EngagementId)),
        new("Client", data.Engagement.ClientId),
        new("Responsible staff", data.Engagement.StaffId),
        new("Stage", data.Engagement.Stage),
        new("Status", data.Engagement.Status),
        new($"Created {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(data.Engagement.CreatedAt))
    ]);

    private static ReportSection ReadinessVerdict(ReadinessHandoffReportData data)
    {
        var verdict = data.ReadinessUnavailable
            ? "Not available — readiness service could not be reached"
            : data.IsReady
                ? "Ready for handoff and closure"
                : "Not ready";

        var stateText = data.ReadinessUnavailable
            ? "Unavailable"
            : data.Readiness?.State ?? "Unknown";

        var evaluatedAt = data.Readiness is null ? NotApplicable : ReportValue.FormatDateTime(data.Readiness.EvaluatedAtUtc);

        return new KeyValueSection("Readiness (generation time)",
        [
            new("Verdict", verdict),
            new("State", stateText),
            new($"Evaluated at {ReportTimeZone.ColumnSuffix}", evaluatedAt),
            new("Documents evidence", data.DocumentsUnavailable
                ? "Unavailable at generation time"
                : $"{data.Documents.Count} document(s) on record")
        ]);
    }

    private static ReportSection ReadinessBlockers(ReadinessHandoffReportData data)
    {
        const string title = "Readiness blockers";
        if (data.ReadinessUnavailable)
        {
            return new EmptySection(title, "Readiness could not be evaluated. The report cannot confirm the engagement is ready.");
        }

        var blockers = data.Readiness?.Blockers;
        if (blockers is null || blockers.Count == 0)
        {
            return new EmptySection(title, "No blockers — the engagement is ready for handoff.");
        }

        return new TableSection(title,
            [new ReportColumn("Kind", Width: 1.4f), new ReportColumn("Blocker", Width: 2), new ReportColumn("Reason", Width: 3)],
            blockers.Select(b => new object?[] { b.Kind, b.Title, b.Reason }));
    }

    private static ReportSection DocumentVerification(ReadinessHandoffReportData data)
    {
        const string title = "Document verification summary";
        if (data.DocumentsUnavailable)
        {
            return new EmptySection(title,
                "The Documents service was unavailable at generation time. Document verification could not be confirmed and the readiness verdict is not upgraded.");
        }
        if (data.Documents.Count == 0)
        {
            return new EmptySection(title, "No documents are on record for this engagement.");
        }

        return new TableSection(title,
            [
                new ReportColumn("Type", Width: 1.8f),
                new ReportColumn("Compliance", Width: 1.4f),
                new ReportColumn("Verification", Width: 1.4f),
                new ReportColumn("Notes", Width: 2)
            ],
            data.Documents.Select(d => new object?[]
            {
                d.Type,
                d.ComplianceStatus,
                d.VerificationStatus,
                d.Notes ?? string.Empty
            }));
    }

    private static ReportSection ApprovalConditions(ReadinessHandoffReportData data)
    {
        const string title = "Approval conditions";
        if (data.ApprovalConditions.Count == 0)
        {
            return new EmptySection(title, "No approval conditions are configured for this engagement.");
        }

        return new TableSection(title,
            [
                new ReportColumn("Title", Width: 2),
                new ReportColumn("Status", Width: 1.2f),
                new ReportColumn("Approval status", Width: 1.4f),
                new ReportColumn($"Satisfied at {ReportTimeZone.ColumnSuffix}", Width: 1.6f),
                new ReportColumn("Rejection reason", Width: 2)
            ],
            data.ApprovalConditions.Select(c => new object?[]
            {
                c.Title,
                c.IsActive ? c.Status : $"{c.Status} (inactive)",
                c.ApprovalStatus ?? NotApplicable,
                c.SatisfiedAt is { } satisfiedAt ? ReportValue.FormatDateTime(satisfiedAt) : NotApplicable,
                c.RejectionReason ?? string.Empty
            }));
    }

    private static ReportSection PaymentConditions(ReadinessHandoffReportData data)
    {
        const string title = "Payment conditions";
        if (data.PaymentConditions.Count == 0)
        {
            return new EmptySection(title, "No payment conditions are configured for this engagement.");
        }

        return new TableSection(title,
            [
                new ReportColumn("Title", Width: 2),
                new ReportColumn("Amount", Width: 1.2f),
                new ReportColumn("Currency", Width: 1),
                new ReportColumn("Status", Width: 1.2f),
                new ReportColumn($"Satisfied at {ReportTimeZone.ColumnSuffix}", Width: 1.6f)
            ],
            data.PaymentConditions.Select(c => new object?[]
            {
                c.Title,
                c.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? NotApplicable,
                c.Currency ?? NotApplicable,
                c.IsActive ? c.Status : $"{c.Status} (inactive)",
                c.SatisfiedAt is { } satisfiedAt ? ReportValue.FormatDateTime(satisfiedAt) : NotApplicable
            }));
    }

    private static ReportSection HandoffContext(ReadinessHandoffReportData data)
    {
        const string title = "Handoff (persisted)";
        if (data.Handoff is null)
        {
            return new EmptySection(title, "The engagement has not been handed off yet.");
        }

        return new KeyValueSection(title,
        [
            new("Handed off by", data.Handoff.HandedOffBy),
            new($"Handed off at {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(data.Handoff.HandedOffAtUtc)),
            new("Summary", string.IsNullOrWhiteSpace(data.Handoff.Summary) ? "(none provided)" : data.Handoff.Summary)
        ]);
    }

    private static ReportSection ClosureContext(ReadinessHandoffReportData data)
    {
        const string title = "Closure (persisted)";
        if (data.Closure is null)
        {
            return new EmptySection(title, "The engagement has not been closed yet.");
        }

        return new KeyValueSection(title,
        [
            new("Closed by", data.Closure.ClosedBy),
            new($"Closed at {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(data.Closure.ClosedAt)),
            new("Summary", string.IsNullOrWhiteSpace(data.Closure.Summary) ? "(none provided)" : data.Closure.Summary)
        ]);
    }

    private static ReportSection Definitions() => new TextSection("Definitions",
    [
        "Readiness verdict: taken live from the same CSTD-20 readiness service the workspace UI uses; this report does not re-derive it.",
        "Readiness blockers: the exact blockers the readiness service reported at generation time. When the readiness service is unreachable, no verdict is claimed.",
        "Document verification summary: the compliance and verification status of each document on record. When the Documents service is unavailable, verification cannot be confirmed and the top-line verdict is forced to Not ready.",
        "Approval and payment conditions: staff-configured Approval and Payment gates. Inactive conditions are still listed (marked inactive) but never affect the readiness verdict.",
        "Handoff and closure: the persisted handoff/closure context recorded by CSTD-20. Kept distinct from the readiness verdict, which is a generation-time state.",
        $"All timestamps are {ReportTimeZone.Description}."
    ]);
}